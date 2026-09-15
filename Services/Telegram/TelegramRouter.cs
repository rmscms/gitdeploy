using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    public sealed class TelegramRouter
    {
        public const string CallbackProjectPrefix = "p:";
        public const string CallbackMenuProjects = "menu:projects";
        public const string CallbackMenuHelp = "menu:help";
        public const string CallbackMenuStatus = "menu:status";
        public const string CallbackRestartAsk = "agent:restart:ask";
        public const string CallbackRestartYes = "agent:restart:yes";
        public const string CallbackRestartNo = "agent:restart:no";
        public const string CallbackModelMenu = "agent:model:menu";
        public const string CallbackModelRefresh = "agent:model:refresh";
        public const string CallbackModelPrefix = "agent:model:";
        public const string CallbackModelFamilyPrefix = "agent:mfam:";

        private readonly TelegramChatStore _store;
        private readonly TelegramBotClient _client;
        private readonly ConfigurationService _config = new();

        public TelegramRouter(TelegramChatStore store, TelegramBotClient client)
        {
            _store = store;
            _client = client;
        }

        public async Task HandleIncomingAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.ChatId == 0 || update.UserId == 0)
            {
                return;
            }

            var config = _config.LoadGlobalConfig();
            if (!IsAllowed(update.UserId, config.TelegramAllowedUserIds))
            {
                return;
            }

            _store.SetLastChatId(update.ChatId);

            if (update.IsCallback)
            {
                await HandleCallbackAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            var text = (update.Text ?? string.Empty).Trim();
            if (IsProjectsCommand(text))
            {
                await ReplyProjectsAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsHelpCommand(text) || IsStartCommand(text))
            {
                await ReplyHomeAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsStatusCommand(text))
            {
                await ReplyStatusAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsDeployCommand(text))
            {
                await ReplyDeployAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsClearLocalCommand(text))
            {
                await ReplyClearLocalAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsWipeTelegramCommand(text))
            {
                await ReplyWipeTelegramAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsRestartAgentCommand(text))
            {
                await ReplyRestartAgentAskAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsModelCommand(text) || text.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyModelMenuOrSetAsync(token, update, text, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (text.StartsWith("/project", StringComparison.OrdinalIgnoreCase))
            {
                var name = text["/project".Length..].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    await ReplyProjectsAsync(token, update, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await SwitchProjectByNameAsync(token, update, name, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (text.StartsWith('/'))
            {
                return;
            }

            var body = string.IsNullOrWhiteSpace(text) ? (update.Caption ?? string.Empty).Trim() : text;
            if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(update.PhotoFileId))
            {
                return;
            }

            var projectPath = ResolveInboundProjectPath(config);
            var photoPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(update.PhotoFileId))
            {
                var dest = System.IO.Path.Combine(
                    TelegramPaths.MediaFolder(projectPath),
                    $"{update.MessageId}.jpg");
                try
                {
                    photoPath = await _client.DownloadFileAsync(token, update.PhotoFileId, dest, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    photoPath = string.Empty;
                }
            }

            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Incoming,
                Status = TelegramMessageStatus.Received,
                Text = body,
                PhotoPath = photoPath,
                TelegramFileId = update.PhotoFileId ?? string.Empty,
                TelegramMessageId = update.MessageId,
                ChatId = update.ChatId,
                UserId = update.UserId,
                SenderName = update.UserName ?? string.Empty,
                Utc = DateTime.UtcNow
            };

            _store.Append(projectPath, message);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);
            CursorAgentBridge.Instance.EnqueueUserTurn(projectPath, body, photoPath);
        }

        private async Task HandleCallbackAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var data = (update.CallbackData ?? string.Empty).Trim();
            try
            {
                if (string.Equals(data, CallbackMenuProjects, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyProjectsAsync(token, update, cancellationToken).ConfigureAwait(false);
                    await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(data, CallbackMenuHelp, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyHomeAsync(token, update, cancellationToken).ConfigureAwait(false);
                    await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(data, CallbackMenuStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyStatusAsync(token, update, cancellationToken).ConfigureAwait(false);
                    await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(data, CallbackRestartAsk, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackRestartYes, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackRestartNo, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackModelMenu, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackModelRefresh, StringComparison.OrdinalIgnoreCase)
                    || data.StartsWith(CallbackModelFamilyPrefix, StringComparison.OrdinalIgnoreCase)
                    || data.StartsWith(CallbackModelPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAgentCallbackAsync(token, update, data, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (data.StartsWith(CallbackProjectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var key = data[CallbackProjectPrefix.Length..];
                    if (!TryResolveProjectByKey(key, out var path))
                    {
                        await _client.AnswerCallbackQueryAsync(
                            token,
                            update.CallbackQueryId,
                            Loc.T("telegram.projectNotFound", key),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await SwitchToProjectAsync(token, update, path, cancellationToken)
                        .ConfigureAwait(false);
                    await _client.AnswerCallbackQueryAsync(
                        token,
                        update.CallbackQueryId,
                        Loc.T("telegram.projectSwitched", TelegramPaths.DisplayName(path)),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task ReplyHomeAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var name = TelegramPaths.DisplayName(active);
            var pending = await TelegramDeployCoordinator.GetPendingChangeCountAsync(active).ConfigureAwait(false);
            var pendingLine = pending > 0
                ? "\n" + TelegramMarkup.Html(Loc.T("telegram.deployReadyHint", pending))
                : string.Empty;
            var html =
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.homeTitle"))}</b>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.homeActive"))} <code>{TelegramMarkup.Html(name)}</code>" +
                pendingLine + "\n\n" +
                TelegramMarkup.Html(Loc.T("telegram.homeBody"));

            // Clear stuck keyboard first, then attach the full pad (with Deploy).
            try
            {
                var resetId = await _client.SendMessageAsync(
                    token,
                    update.ChatId,
                    Loc.T("telegram.keyboardResetting"),
                    cancellationToken,
                    TelegramMarkup.RemoveReplyKeyboard()).ConfigureAwait(false);
                _store.TrackBotMessageId(active, resetId);
            }
            catch
            {
            }

            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ReplyStatusAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var status = CursorAgentBridge.Instance.GetAgentStatus(active);
            var pending = await TelegramDeployCoordinator.GetPendingChangeCountAsync(active).ConfigureAwait(false);
            var lastLine = status.LastActivityUtc.HasValue
                ? status.LastActivityUtc.Value.ToLocalTime().ToString("HH:mm:ss")
                : "—";
            var html =
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.statusTitle"))}</b>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.homeActive"))} <code>{TelegramMarkup.Html(TelegramPaths.DisplayName(active))}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusAgent"))}: " +
                (status.Enabled
                    ? (status.DaemonAlive ? Loc.T("telegram.statusAlive") : Loc.T("telegram.statusDead"))
                    : Loc.T("telegram.statusDisabled")) + "\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusModel"))}: <code>{TelegramMarkup.Html(status.Model)}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusQueue"))}: {status.QueueDepth}" +
                (status.TurnBusy ? " (" + TelegramMarkup.Html(Loc.T("telegram.statusBusy")) + ")" : string.Empty) + "\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusSession"))}: <code>{TelegramMarkup.Html(status.SessionHint)}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusLast"))}: {TelegramMarkup.Html(lastLine)}";

            if (pending > 0)
            {
                html += "\n" + TelegramMarkup.Html(Loc.T("telegram.deployReadyHint", pending));
            }

            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramMarkup.Inline(
                    new[]
                    {
                        (Loc.T("telegram.btnRestartAgent"), CallbackRestartAsk),
                        (Loc.T("telegram.btnModel"), CallbackModelMenu)
                    }),
                cancellationToken,
                active).ConfigureAwait(false);

            // Also refresh reply keyboard so new agent buttons appear.
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.keyboardReady")),
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyRestartAgentAskAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.restartConfirm")),
                TelegramMarkup.Inline(
                    new[]
                    {
                        (Loc.T("telegram.restartYes"), CallbackRestartYes),
                        (Loc.T("telegram.restartNo"), CallbackRestartNo)
                    }),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyModelMenuOrSetAsync(
            string token,
            TelegramIncomingUpdate update,
            string text,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            if (text.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
            {
                var arg = text["/model".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(arg))
                {
                    if (string.Equals(arg, "default", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(arg, "auto", StringComparison.OrdinalIgnoreCase)
                        || arg == "-")
                    {
                        CursorAgentBridge.Instance.SetModelAndRestart(active, "auto");
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", "auto")),
                            TelegramDeployCoordinator.BuildReplyKeyboard(),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    var matches = await CursorModelCatalog.Instance
                        .SearchAsync(arg, cancellationToken)
                        .ConfigureAwait(false);
                    var exact = matches.FirstOrDefault(m =>
                        string.Equals(m.Id, arg, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(exact.Id))
                    {
                        CursorAgentBridge.Instance.SetModelAndRestart(active, exact.Id);
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", exact.Id)),
                            TelegramDeployCoordinator.BuildReplyKeyboard(),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    if (matches.Count == 1)
                    {
                        CursorAgentBridge.Instance.SetModelAndRestart(active, matches[0].Id);
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", matches[0].Id)),
                            TelegramDeployCoordinator.BuildReplyKeyboard(),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    if (matches.Count > 1)
                    {
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("telegram.modelSearchHits", matches.Count, arg)),
                            BuildModelButtons(matches.Take(18).ToList()),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    // Unknown to catalog — still try (CLI may accept it).
                    CursorAgentBridge.Instance.SetModelAndRestart(active, arg);
                    await SendBotAsync(
                        token,
                        update.ChatId,
                        TelegramMarkup.Html(Loc.T("cursor.modelSetUnknown", arg)),
                        TelegramDeployCoordinator.BuildReplyKeyboard(),
                        cancellationToken,
                        active).ConfigureAwait(false);
                    return;
                }
            }

            await SendModelRootMenuAsync(token, update.ChatId, active, forceRefresh: false, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task SendModelRootMenuAsync(
            string token,
            long chatId,
            string active,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            var models = await CursorModelCatalog.Instance
                .GetModelsAsync(forceRefresh, cancellationToken)
                .ConfigureAwait(false);
            var err = CursorModelCatalog.Instance.LastError;
            var body = models.Count == 0
                ? Loc.T("telegram.modelListFailed", err ?? "?")
                : Loc.T("telegram.modelPickLive", models.Count);

            await SendBotAsync(
                token,
                chatId,
                TelegramMarkup.Html(body),
                await BuildModelRootInlineAsync(models, cancellationToken).ConfigureAwait(false),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task HandleAgentCallbackAsync(
            string token,
            TelegramIncomingUpdate update,
            string data,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            if (string.Equals(data, CallbackRestartAsk, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyRestartAgentAskAsync(token, update, cancellationToken).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(data, CallbackRestartNo, StringComparison.OrdinalIgnoreCase))
            {
                await _client.AnswerCallbackQueryAsync(
                    token,
                    update.CallbackQueryId,
                    Loc.T("telegram.restartCancelled"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(data, CallbackRestartYes, StringComparison.OrdinalIgnoreCase))
            {
                CursorAgentBridge.Instance.CancelCurrentTurn(active);
                CursorAgentBridge.Instance.RestartProjectAgent(active);
                await _client.AnswerCallbackQueryAsync(
                    token,
                    update.CallbackQueryId,
                    Loc.T("cursor.restarted"),
                    cancellationToken).ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("cursor.restarted")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            if (string.Equals(data, CallbackModelMenu, StringComparison.OrdinalIgnoreCase)
                || string.Equals(data, CallbackModelRefresh, StringComparison.OrdinalIgnoreCase))
            {
                await SendModelRootMenuAsync(
                        token,
                        update.ChatId,
                        active,
                        forceRefresh: string.Equals(data, CallbackModelRefresh, StringComparison.OrdinalIgnoreCase),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (data.StartsWith(CallbackModelFamilyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var family = data[CallbackModelFamilyPrefix.Length..].Trim().ToLowerInvariant();
                var all = await CursorModelCatalog.Instance
                    .GetModelsAsync(forceRefresh: false, cancellationToken)
                    .ConfigureAwait(false);
                var subset = all
                    .Where(m => string.Equals(m.Family, family, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(24)
                    .ToList();
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.modelFamily", family, subset.Count)),
                    BuildModelButtons(subset),
                    cancellationToken,
                    active).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (data.StartsWith(CallbackModelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var modelKey = data[CallbackModelPrefix.Length..];
                if (string.Equals(modelKey, "menu", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modelKey, "refresh", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var model = string.Equals(modelKey, "default", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : modelKey;
                CursorAgentBridge.Instance.SetModelAndRestart(active, model);
                var msg = Loc.T("cursor.modelSet", model);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, msg, cancellationToken)
                    .ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(msg),
                    TelegramDeployCoordinator.BuildReplyKeyboard(),
                    cancellationToken,
                    active).ConfigureAwait(false);
            }
        }

        private static Task<JObject> BuildModelRootInlineAsync(
            IReadOnlyList<CursorModelInfo> models,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var families = models
                .Select(m => m.Family)
                .Where(f => !string.Equals(f, "auto", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PreferredFamilyOrder)
                .ToList();

            var rows = new List<(string Text, string Data)[]>
            {
                new[]
                {
                    (Loc.T("telegram.modelDefault"), CallbackModelPrefix + "auto"),
                    (Loc.T("telegram.modelRefresh"), CallbackModelRefresh)
                }
            };

            // Family chips, 3 per row.
            for (var i = 0; i < families.Count; i += 3)
            {
                var chunk = families.Skip(i).Take(3)
                    .Select(f => (FamilyLabel(f), CallbackModelFamilyPrefix + f))
                    .ToArray();
                rows.Add(chunk);
            }

            return Task.FromResult(TelegramMarkup.Inline(rows.ToArray()));
        }

        private static JObject BuildModelButtons(IReadOnlyList<CursorModelInfo> models)
        {
            var rows = new List<(string Text, string Data)[]>();
            var pair = new List<(string Text, string Data)>();
            foreach (var model in models)
            {
                var data = CallbackModelPrefix + model.Id;
                if (data.Length > 64)
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName;
                if (label.Length > 28)
                {
                    label = label[..25] + "…";
                }

                pair.Add((label, data));
                if (pair.Count == 2)
                {
                    rows.Add(pair.ToArray());
                    pair.Clear();
                }
            }

            if (pair.Count > 0)
            {
                rows.Add(pair.ToArray());
            }

            rows.Add(new[] { (Loc.T("telegram.btnModel"), CallbackModelMenu) });
            return TelegramMarkup.Inline(rows.ToArray());
        }

        private static int PreferredFamilyOrder(string family)
        {
            return family.ToLowerInvariant() switch
            {
                "grok" => 0,
                "composer" => 1,
                "claude" => 2,
                "gpt" => 3,
                "codex" => 4,
                "gemini" => 5,
                _ => 9
            };
        }

        private static string FamilyLabel(string family)
        {
            return family.ToLowerInvariant() switch
            {
                "grok" => "Grok",
                "composer" => "Composer",
                "claude" => "Claude",
                "gpt" => "GPT",
                "codex" => "Codex",
                "gemini" => "Gemini",
                _ => family
            };
        }

        private async Task ReplyDeployAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            if (TelegramPaths.IsUnassigned(active))
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.deployNoProject")),
                    await TelegramDeployCoordinator.BuildReplyKeyboardAsync(active).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            TelegramProjectSync.ApplyToGitDeploy(active);

            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.deployStarting")),
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken).ConfigureAwait(false);

            var result = await TelegramDeployCoordinator.RunAsync(active).ConfigureAwait(false);
            var icon = result.Ok ? "✅" : "❌";
            var html = $"{icon} {TelegramMarkup.Html(result.Summary)}";

            // Mirror into in-app project chat
            var chatMsg = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Outgoing,
                Status = TelegramMessageStatus.Sent,
                Text = $"{icon} {result.Summary}",
                Utc = DateTime.UtcNow,
                SenderName = "GitDeploy"
            };
            _store.Append(active, chatMsg);
            TelegramPoller.Instance.RaiseMessage(active, chatMsg);

            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ReplyClearLocalAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var cleared = TelegramChatCleaner.ClearLocal(active);
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(TelegramChatCleaner.LocalSummary(cleared)),
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyWipeTelegramAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);

            // Include the wipe command message itself.
            if (update.MessageId > 0)
            {
                _store.TrackBotMessageId(active, update.MessageId);
            }

            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.wipeTelegramStarting")),
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken,
                active).ConfigureAwait(false);

            var (deleted, localCleared) = await TelegramChatCleaner.WipeTelegramAndLocalAsync(
                token,
                update.ChatId,
                active,
                _client,
                cancellationToken).ConfigureAwait(false);

            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(TelegramChatCleaner.WipeSummary(deleted, localCleared)),
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyProjectsAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var projects = ListRecentProjects();
            if (projects.Count == 0)
            {
            await SendBotAsync(
                token,
                update.ChatId,
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.projectsTitle"))}</b>\n{TelegramMarkup.Html(Loc.T("telegram.noProjects"))}",
                await TelegramDeployCoordinator.BuildReplyKeyboardAsync(active).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
                return;
            }

            var rows = new List<IReadOnlyList<(string Text, string Data)>>();
            foreach (var path in projects)
            {
                var name = TelegramPaths.DisplayName(path);
                var isActive = string.Equals(path, active, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(
                                   TelegramPaths.ToProjectKey(path),
                                   TelegramPaths.ToProjectKey(active),
                                   StringComparison.OrdinalIgnoreCase);
                var label = isActive
                    ? Loc.T("telegram.projectButtonActive", name)
                    : Loc.T("telegram.projectButton", name);
                rows.Add(new[] { (label, CallbackProjectPrefix + TelegramPaths.ToProjectKey(path)) });
            }

            rows.Add(new[] { (Loc.T("telegram.btnStatus"), CallbackMenuStatus) });

            await SendBotAsync(
                token,
                update.ChatId,
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.projectsTitle"))}</b>\n{TelegramMarkup.Html(Loc.T("telegram.projectsHint"))}",
                TelegramMarkup.InlineRows(rows),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SwitchProjectByNameAsync(
            string token,
            TelegramIncomingUpdate update,
            string name,
            CancellationToken cancellationToken)
        {
            if (!TryResolveProjectByName(name, out var path))
            {
                await ReplyProjectsAsync(token, update, cancellationToken).ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.projectNotFound", name)),
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await SwitchToProjectAsync(token, update, path, cancellationToken).ConfigureAwait(false);
        }

        private async Task SwitchToProjectAsync(
            string token,
            TelegramIncomingUpdate update,
            string path,
            CancellationToken cancellationToken)
        {
            TelegramProjectSync.ApplyToGitDeploy(path);
            var pending = await TelegramDeployCoordinator.GetPendingChangeCountAsync(path).ConfigureAwait(false);
            var switched = Loc.T("telegram.projectSwitched", TelegramPaths.DisplayName(path));
            var pendingLine = pending > 0
                ? "\n" + TelegramMarkup.Html(Loc.T("telegram.deployReadyHint", pending))
                : string.Empty;
            var html =
                $"✅ <b>{TelegramMarkup.Html(switched)}</b>" +
                pendingLine + "\n\n" +
                TelegramMarkup.Html(Loc.T("telegram.homeBody"));
            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramDeployCoordinator.BuildReplyKeyboard(),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SendBotAsync(
            string token,
            long chatId,
            string html,
            JToken? markup,
            CancellationToken cancellationToken,
            string? projectPath = null)
        {
            var trackPath = string.IsNullOrWhiteSpace(projectPath)
                ? _store.GetActiveProjectPath()
                : projectPath;
            try
            {
                var messageId = await _client.SendMessageAsync(
                    token,
                    chatId,
                    html,
                    cancellationToken,
                    markup ?? TelegramDeployCoordinator.BuildReplyKeyboard(),
                    "HTML").ConfigureAwait(false);
                _store.TrackBotMessageId(trackPath, messageId);
            }
            catch
            {
                try
                {
                    var messageId = await _client.SendMessageAsync(
                        token,
                        chatId,
                        html,
                        cancellationToken,
                        TelegramDeployCoordinator.BuildReplyKeyboard()).ConfigureAwait(false);
                    _store.TrackBotMessageId(trackPath, messageId);
                }
                catch
                {
                }
            }
        }

        private JObject HomeInline()
        {
            return TelegramMarkup.Inline(
                new[]
                {
                    (Loc.T("telegram.btnProjects"), CallbackMenuProjects)
                },
                new[]
                {
                    (Loc.T("telegram.btnHelp"), CallbackMenuHelp)
                });
        }

        public bool TryResolveProjectByName(string name, out string path)
        {
            path = string.Empty;
            var needle = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(needle))
            {
                return false;
            }

            var projects = ListRecentProjects();
            var exact = projects.FirstOrDefault(p =>
                string.Equals(TelegramPaths.DisplayName(p), needle, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                path = exact;
                return true;
            }

            var starts = projects.Where(p =>
                    TelegramPaths.DisplayName(p).StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (starts.Count == 1)
            {
                path = starts[0];
                return true;
            }

            var contains = projects.Where(p =>
                    TelegramPaths.DisplayName(p).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (contains.Count == 1)
            {
                path = contains[0];
                return true;
            }

            return false;
        }

        public bool TryResolveProjectByKey(string key, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            foreach (var candidate in ListRecentProjects())
            {
                if (string.Equals(TelegramPaths.ToProjectKey(candidate), key, StringComparison.OrdinalIgnoreCase))
                {
                    path = candidate;
                    return true;
                }
            }

            var threads = _store.ListThreads(ListRecentProjects());
            foreach (var thread in threads)
            {
                if (string.Equals(TelegramPaths.ToProjectKey(thread.ProjectPath), key, StringComparison.OrdinalIgnoreCase)
                    && !TelegramPaths.IsUnassigned(thread.ProjectPath))
                {
                    path = thread.ProjectPath;
                    return true;
                }
            }

            return false;
        }

        public static bool IsAllowed(long userId, string? allowedCsv)
        {
            if (string.IsNullOrWhiteSpace(allowedCsv) || userId == 0)
            {
                return false;
            }

            var parts = allowedCsv.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (long.TryParse(part.Trim(), out var id) && id == userId)
                {
                    return true;
                }
            }

            return false;
        }

        private List<string> ListRecentProjects()
        {
            return (_config.LoadGlobalConfig().RecentProjects ?? new())
                .Where(p => !string.IsNullOrWhiteSpace(p.Path))
                .Select(p => p.Path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string ResolveInboundProjectPath(ConfigurationService.GlobalConfig config)
        {
            var active = _store.GetActiveProjectPath();
            if (!TelegramPaths.IsUnassigned(active))
            {
                return active;
            }

            if (!string.IsNullOrWhiteSpace(config.TelegramActiveProjectPath)
                && !TelegramPaths.IsUnassigned(config.TelegramActiveProjectPath))
            {
                return config.TelegramActiveProjectPath;
            }

            if (!string.IsNullOrWhiteSpace(config.LastProjectPath))
            {
                return config.LastProjectPath;
            }

            return TelegramPaths.UnassignedPath;
        }

        private static bool IsProjectsCommand(string text)
            => MatchesCommand(text, "/projects", "telegram.kbProjects", "telegram.btnProjects", "projects", "پروژه‌ها");

        private static bool IsHelpCommand(string text)
            => MatchesCommand(text, "/help", "telegram.kbHelp", "telegram.btnHelp", "help", "راهنما");

        private static bool IsStartCommand(string text)
            => text.StartsWith("/start", StringComparison.OrdinalIgnoreCase);

        private static bool IsStatusCommand(string text)
            => MatchesCommand(text, "/status", "telegram.kbStatus", "telegram.btnStatus", "status", "وضعیت");

        private static bool IsDeployCommand(string text)
            => MatchesCommand(
                text,
                "/deploy",
                "telegram.kbDeploy",
                "telegram.btnDeploy",
                "deploy",
                "دیپلوی",
                "دیپلوی کن",
                "دپلی",
                "دپلی کن");

        private static bool IsClearLocalCommand(string text)
            => MatchesCommand(
                text,
                "/clear",
                "telegram.kbClear",
                "telegram.btnClear",
                "clear",
                "پاک کردن چت",
                "پاک کردن");

        private static bool IsWipeTelegramCommand(string text)
            => MatchesCommand(
                text,
                "/wipe",
                "telegram.kbWipeTelegram",
                "telegram.btnWipeTelegram",
                "wipe",
                "پاک تلگرام",
                "پاک کردن تلگرام");

        private static bool IsRestartAgentCommand(string text)
            => MatchesCommand(
                text,
                "/restart",
                "telegram.kbRestartAgent",
                "telegram.btnRestartAgent",
                "restart",
                "ریستارت",
                "ریستارت ایجنت");

        private static bool IsModelCommand(string text)
            => MatchesCommand(
                text,
                "/models",
                "telegram.kbModel",
                "telegram.btnModel",
                "model",
                "مدل");

        private static bool MatchesCommand(string text, string slash, params string[] labels)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.StartsWith(slash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalized = NormalizeButton(text);
            foreach (var label in labels)
            {
                var resolved = label.StartsWith("telegram.", StringComparison.Ordinal)
                    ? Loc.T(label)
                    : label;
                if (string.Equals(normalized, NormalizeButton(resolved), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeButton(string value)
        {
            var chars = (value ?? string.Empty)
                .Trim()
                .Replace("📂", "")
                .Replace("ℹ️", "")
                .Replace("●", "")
                .Replace("✅", "")
                .Replace("🚀", "")
                .Replace("🧹", "")
                .Replace("🗑", "")
                .Replace("♻️", "")
                .Replace("🧠", "")
                .Trim();
            return chars;
        }
    }
}
