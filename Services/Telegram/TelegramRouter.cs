using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public const string CallbackWorkspaceRoots = "agent:workspace:roots";
        public const string CallbackStopTurn = "agent:stop";
        public const string CallbackEngineMenu = "agent:engine:menu";
        public const string CallbackEngineCursor = "agent:engine:cursor";
        public const string CallbackEngineCodex = "agent:engine:codex";
        public const string CallbackCursorCache = "cache:menu";
        public const string CallbackCursorCacheSafe = "cache:safe";
        public const string CallbackCursorCacheSafeForce = "cache:safe:force";
        public const string CallbackCursorCacheAgg = "cache:agg";
        public const string CallbackCursorCacheAggForce = "cache:agg:force";

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

            if (IsPlansCommand(text))
            {
                await ReplyPlansAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsPlanModeCommand(text))
            {
                await ReplySetCursorModeAsync(token, update, "plan", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsAgentModeCommand(text))
            {
                await ReplySetCursorModeAsync(token, update, "agent", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsDeployCommand(text))
            {
                await ReplyDeployAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsPreviewCommand(text))
            {
                await ReplyPreviewAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsEngineCommand(text))
            {
                await ReplyEngineMenuAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsClearLocalCommand(text))
            {
                await ReplyClearLocalAsync(token, update, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsCursorCacheCommand(text))
            {
                await ReplyCursorCacheAsync(token, update, cancellationToken).ConfigureAwait(false);
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

            if (IsStopAgentCommand(text))
            {
                await ReplyStopAgentAsync(token, update, cancellationToken).ConfigureAwait(false);
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
            var hasPhoto = !string.IsNullOrWhiteSpace(update.PhotoFileId);
            var hasDocument = !string.IsNullOrWhiteSpace(update.DocumentFileId);
            if (string.IsNullOrWhiteSpace(body) && !hasPhoto && !hasDocument)
            {
                return;
            }

            var projectPath = ResolveInboundProjectPath(config);
            var photoPath = string.Empty;
            var attachmentPath = string.Empty;
            var attachmentName = string.Empty;
            var agentText = body;

            if (hasPhoto)
            {
                var dest = Path.Combine(
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

            if (hasDocument)
            {
                var fileName = string.IsNullOrWhiteSpace(update.DocumentFileName)
                    ? $"document-{update.MessageId}.bin"
                    : update.DocumentFileName.Trim();
                if (!IsAllowedTextDocument(fileName, update.DocumentMimeType))
                {
                    await SendBotAsync(
                        token,
                        update.ChatId,
                        TelegramMarkup.Html(Loc.T("telegram.documentUnsupported")),
                        TelegramDeployCoordinator.BuildReplyKeyboard(projectPath),
                        cancellationToken,
                        projectPath).ConfigureAwait(false);
                    return;
                }

                var safeName = SanitizeFileName(fileName);
                var dest = Path.Combine(
                    TelegramPaths.MediaFolder(projectPath),
                    $"{update.MessageId}-{safeName}");
                try
                {
                    attachmentPath = await _client.DownloadFileAsync(
                            token,
                            update.DocumentFileId,
                            dest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attachmentName = Path.GetFileName(attachmentPath);
                    agentText = BuildAgentTextWithDocument(body, attachmentPath, attachmentName);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        body = Loc.T("telegram.document", attachmentName);
                    }
                }
                catch
                {
                    attachmentPath = string.Empty;
                    attachmentName = string.Empty;
                    await SendBotAsync(
                        token,
                        update.ChatId,
                        TelegramMarkup.Html(Loc.T("telegram.documentDownloadFailed")),
                        TelegramDeployCoordinator.BuildReplyKeyboard(projectPath),
                        cancellationToken,
                        projectPath).ConfigureAwait(false);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(agentText) && string.IsNullOrWhiteSpace(photoPath))
            {
                return;
            }

            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Incoming,
                Status = TelegramMessageStatus.Received,
                Text = body,
                PhotoPath = photoPath,
                AttachmentPath = attachmentPath,
                AttachmentName = attachmentName,
                TelegramFileId = !string.IsNullOrWhiteSpace(update.PhotoFileId)
                    ? update.PhotoFileId
                    : (update.DocumentFileId ?? string.Empty),
                TelegramMessageId = update.MessageId,
                ChatId = update.ChatId,
                UserId = update.UserId,
                SenderName = update.UserName ?? string.Empty,
                Utc = DateTime.UtcNow
            };

            _store.Append(projectPath, message);
            TelegramPoller.Instance.RaiseMessage(projectPath, message);
            AgentFacade.EnqueueUserTurn(projectPath, agentText, photoPath);
        }

        private static bool IsAllowedTextDocument(string fileName, string? mimeType)
        {
            var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (ext is ".txt" or ".md" or ".markdown" or ".mdc")
            {
                return true;
            }

            var mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
            if (mime is "text/plain" or "text/markdown" or "text/x-markdown" or "application/markdown")
            {
                return true;
            }

            return false;
        }

        private static string SanitizeFileName(string fileName)
        {
            var name = Path.GetFileName(fileName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return "document.txt";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Length <= 120 ? name : name[..120];
        }

        private static string BuildAgentTextWithDocument(string captionOrText, string filePath, string displayName)
        {
            const int maxChars = 120_000;
            var sb = new StringBuilder();
            var intro = (captionOrText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(intro))
            {
                sb.AppendLine(intro);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"User attached a text file ({displayName}). Read it and follow the request inside (or report briefly).");
                sb.AppendLine();
            }

            sb.AppendLine($"--- Attached file: {displayName} ---");
            try
            {
                var content = File.ReadAllText(filePath, Encoding.UTF8);
                if (content.Length > maxChars)
                {
                    sb.AppendLine(content[..maxChars]);
                    sb.AppendLine();
                    sb.AppendLine($"--- truncated after {maxChars} characters ---");
                }
                else
                {
                    sb.AppendLine(content);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(Could not read attached file: {ex.Message})");
                sb.AppendLine($"File path on disk: {filePath}");
            }

            sb.AppendLine("--- end attached file ---");
            sb.AppendLine($"Local copy: {filePath}");
            return sb.ToString().Trim();
        }

        private async Task HandleCallbackAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var data = (update.CallbackData ?? string.Empty).Trim();
            try
            {
                if (CursorAskQuestionBroker.Instance.TryAnswer(data, out var askAck))
                {
                    await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, askAck, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (TelegramPlanMdBroker.Instance.IsPlanMdCallback(data))
                {
                    var (handled, ack) = await TelegramPlanMdBroker.Instance
                        .TryHandleAsync(token, update.ChatId, data, update.Text, cancellationToken)
                        .ConfigureAwait(false);
                    if (handled)
                    {
                        await _client.AnswerCallbackQueryAsync(
                                token,
                                update.CallbackQueryId,
                                ack,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                if (TelegramPlanMdBroker.Instance.IsBuildCallback(data))
                {
                    var (handled, ack) = await TelegramPlanMdBroker.Instance
                        .TryHandleBuildAsync(token, update.ChatId, data, update.Text, cancellationToken)
                        .ConfigureAwait(false);
                    if (handled)
                    {
                        await _client.AnswerCallbackQueryAsync(
                                token,
                                update.CallbackQueryId,
                                ack,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                if (TelegramPlanMdBroker.Instance.IsDeleteCallback(data))
                {
                    var (handled, ack) = await TelegramPlanMdBroker.Instance
                        .TryHandleDeleteAsync(token, update.ChatId, data, cancellationToken)
                        .ConfigureAwait(false);
                    if (handled)
                    {
                        await _client.AnswerCallbackQueryAsync(
                                token,
                                update.CallbackQueryId,
                                ack,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                if (string.Equals(data, CallbackCursorCache, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackCursorCacheSafe, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackCursorCacheSafeForce, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackCursorCacheAgg, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackCursorCacheAggForce, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCursorCacheCallbackAsync(token, update, data, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

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
                    || string.Equals(data, CallbackWorkspaceRoots, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackStopTurn, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackEngineMenu, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackEngineCursor, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data, CallbackEngineCodex, StringComparison.OrdinalIgnoreCase)
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
            var kindLine = "\n" + TelegramMarkup.Html(Loc.T(
                "telegram.projectKindActive",
                TelegramProjectProfile.GetKindLabel(active)));
            var html =
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.homeTitle"))}</b>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.homeActive"))} <code>{TelegramMarkup.Html(name)}</code>" +
                kindLine +
                pendingLine + "\n\n" +
                TelegramMarkup.Html(Loc.T("telegram.homeBody"));

            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyPlansAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            if (!TelegramPaths.IsUnassigned(active) && Directory.Exists(active))
            {
                CursorPlanFileWriter.EnsurePlansDirectory(active);
            }

            var plans = CursorPlanCatalog.List(active);
            var latest = CursorPlanCatalog.GetLatest(active);

            if (plans.Count == 0)
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.plansEmpty")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(TelegramMarkup.Html(Loc.T("telegram.plansTitle"))).Append("</b>\n");
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.homeActive"))).Append(" <code>")
                .Append(TelegramMarkup.Html(TelegramPaths.DisplayName(active))).Append("</code>\n\n");

            var rows = new System.Collections.Generic.List<(string Text, string Data)[]>();
            rows.Add(new[]
            {
                (Loc.T("telegram.kbGetPlanMd"), TelegramPlanMdBroker.CallbackPrefix + TelegramPlanMdBroker.LastToken),
                (Loc.T("telegram.kbBuildPlan"), TelegramPlanMdBroker.CallbackBuildPrefix + TelegramPlanMdBroker.LastToken)
            });
            rows.Add(new[]
            {
                (Loc.T("telegram.kbDeleteAllPlans"), TelegramPlanMdBroker.CallbackDeletePrefix + "all")
            });

            for (var i = 0; i < plans.Count && i < 10; i++)
            {
                var path = plans[i];
                var name = Path.GetFileName(path);
                var isLatest = !string.IsNullOrWhiteSpace(latest)
                               && string.Equals(path, latest, StringComparison.OrdinalIgnoreCase);
                var mark = isLatest ? "⭐ " : "• ";
                var when = File.GetLastWriteTime(path).ToString("MM-dd HH:mm");
                sb.Append(mark)
                    .Append("<code>").Append(TelegramMarkup.Html(name)).Append("</code>")
                    .Append(" — ").Append(TelegramMarkup.Html(when))
                    .Append('\n');

                var shortName = name.Length > 22 ? name[..19] + "…" : name;
                rows.Add(new[]
                {
                    ("📄 " + shortName, TelegramPlanMdBroker.CallbackPrefix + "i:" + i),
                    ("🚀", TelegramPlanMdBroker.CallbackBuildPrefix + "i:" + i),
                    ("🗑", TelegramPlanMdBroker.CallbackDeletePrefix + "i:" + i)
                });
            }

            await SendBotAsync(
                token,
                update.ChatId,
                sb.ToString().TrimEnd(),
                TelegramMarkup.Inline(rows.ToArray()),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyStatusAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var status = AgentFacade.GetAgentStatus(active);
            var pending = await TelegramDeployCoordinator.GetPendingChangeCountAsync(active).ConfigureAwait(false);
            var workspace = CursorWorkspaceRoots.GetRoots(active);
            var accountUsage = await CursorAccountUsageService.GetAsync(cancellationToken).ConfigureAwait(false);
            var lastLine = status.LastActivityUtc.HasValue
                ? status.LastActivityUtc.Value.ToLocalTime().ToString("HH:mm:ss")
                : "—";
            var html =
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.statusTitle"))}</b>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.homeActive"))} <code>{TelegramMarkup.Html(TelegramPaths.DisplayName(active))}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.projectKindActive", TelegramProjectProfile.GetKindLabel(active)))}\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.engineActive", AgentFacade.DisplayName(AgentFacade.GetActiveEngine())))}\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusAgent"))}: " +
                (status.Enabled
                    ? (status.DaemonAlive ? Loc.T("telegram.statusAlive") : Loc.T("telegram.statusDead"))
                    : Loc.T("telegram.statusDisabled")) + "\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusModel"))}: <code>{TelegramMarkup.Html(status.Model)}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusQueue"))}: {status.QueueDepth}" +
                (status.TurnBusy ? " (" + TelegramMarkup.Html(Loc.T("telegram.statusBusy")) + ")" : string.Empty) + "\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusSession"))}: <code>{TelegramMarkup.Html(status.SessionHint)}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusMode"))}: <code>{TelegramMarkup.Html(status.AgentMode)}</code>\n" +
                $"{TelegramMarkup.Html(Loc.T("telegram.statusLast"))}: {TelegramMarkup.Html(lastLine)}";

            html += "\n" + FormatAccountUsageHtml(accountUsage);
            html += "\n" + FormatUsageStatusHtml(status);
            html += "\n" + FormatCursorDiskStatusHtml();

            if (workspace.HasMultiRoot)
            {
                html += "\n" +
                    $"{TelegramMarkup.Html(Loc.T("telegram.statusWorkspace"))}: " +
                    TelegramMarkup.Html(Loc.T("telegram.statusWorkspaceFolders", workspace.Extras.Count));
            }

            if (pending > 0)
            {
                html += "\n" + TelegramMarkup.Html(Loc.T("telegram.deployReadyHint", pending));
            }

            var inlineRows = new List<(string Text, string Data)[]>
            {
                new[]
                {
                    (Loc.T("telegram.btnRestartAgent"), CallbackRestartAsk),
                    (Loc.T("telegram.btnModel"), CallbackModelMenu)
                }
            };
            if (status.TurnBusy)
            {
                inlineRows.Add(new[]
                {
                    (Loc.T("telegram.btnStopAgent"), CallbackStopTurn)
                });
            }

            if (workspace.HasMultiRoot)
            {
                inlineRows.Add(new[]
                {
                    (Loc.T("telegram.btnWorkspaceRoots"), CallbackWorkspaceRoots)
                });
            }

            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramMarkup.Inline(inlineRows.ToArray()),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyWorkspaceRootsAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            var workspace = CursorWorkspaceRoots.GetRoots(active);
            if (!workspace.HasMultiRoot)
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.workspace.noExtras")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            var lines = new List<string>
            {
                $"<b>{TelegramMarkup.Html(Loc.T("telegram.workspace.pathsTitle"))}</b>",
                $"{TelegramMarkup.Html(Loc.T("telegram.workspace.primaryLabel"))}: <code>{TelegramMarkup.Html(workspace.Primary)}</code>"
            };
            for (var i = 0; i < workspace.Extras.Count; i++)
            {
                lines.Add($"{i + 1}. <code>{TelegramMarkup.Html(workspace.Extras[i])}</code>");
            }

            await SendBotAsync(
                token,
                update.ChatId,
                string.Join("\n", lines),
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyStopAgentAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            var status = AgentFacade.GetAgentStatus(active);
            if (!status.TurnBusy && status.QueueDepth <= 0)
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.agentNotBusy")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            var stopped = AgentFacade.StopTurn(active);
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(
                    stopped
                        ? Loc.T("telegram.agentStopped")
                        : Loc.T("telegram.agentNotBusy")),
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
                        AgentFacade.SetModelAndRestart(active, "auto");
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", "auto")),
                            TelegramDeployCoordinator.BuildReplyKeyboard(active),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    if (AgentFacade.GetActiveEngine() == AgentEngineKind.Codex)
                    {
                        var hit = CodexModelCatalog.Find(arg);
                        var id = hit?.Id ?? arg;
                        AgentFacade.SetModelAndRestart(active, id);
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", id)),
                            TelegramDeployCoordinator.BuildReplyKeyboard(active),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    var matches = await SearchCursorModelsBoundedAsync(arg, cancellationToken)
                        .ConfigureAwait(false);
                    var exact = matches.FirstOrDefault(m =>
                        string.Equals(m.Id, arg, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(exact.Id))
                    {
                        AgentFacade.SetModelAndRestart(active, exact.Id);
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", exact.Id)),
                            TelegramDeployCoordinator.BuildReplyKeyboard(active),
                            cancellationToken,
                            active).ConfigureAwait(false);
                        return;
                    }

                    if (matches.Count == 1)
                    {
                        AgentFacade.SetModelAndRestart(active, matches[0].Id);
                        await SendBotAsync(
                            token,
                            update.ChatId,
                            TelegramMarkup.Html(Loc.T("cursor.modelSet", matches[0].Id)),
                            TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
                    AgentFacade.SetModelAndRestart(active, arg);
                    await SendBotAsync(
                        token,
                        update.ChatId,
                        TelegramMarkup.Html(Loc.T("cursor.modelSetUnknown", arg)),
                        TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
            if (AgentFacade.GetActiveEngine() == AgentEngineKind.Codex)
            {
                var providerId = CodexProviderCatalog.Normalize(_config.LoadGlobalConfig().CodexProvider);
                var provider = CodexProviderCatalog.Get(providerId);
                var codexModels = CodexModelCatalog.GetModels(providerId);
                var rows = new List<(string Text, string Data)[]>();
                for (var i = 0; i < codexModels.Count; i += 1)
                {
                    var m = codexModels[i];
                    var label = (m.IsFree ? "🆓 " : "💳 ") + TruncateButton(m.Label, 28);
                    rows.Add(new[] { (label, CallbackModelPrefix + m.Id) });
                }

                await SendBotAsync(
                    token,
                    chatId,
                    TelegramMarkup.Html(Loc.T("telegram.modelPickCodex", provider.Label, codexModels.Count)),
                    TelegramMarkup.Inline(rows.ToArray()),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            var models = await GetCursorModelsBoundedAsync(forceRefresh, cancellationToken)
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

        private static async Task<IReadOnlyList<CursorModelInfo>> GetCursorModelsBoundedAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                return await CursorModelCatalog.Instance
                    .GetModelsAsync(forceRefresh, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<CursorModelInfo>();
            }
        }

        private static async Task<IReadOnlyList<CursorModelInfo>> SearchCursorModelsBoundedAsync(
            string query,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                return await CursorModelCatalog.Instance
                    .SearchAsync(query, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<CursorModelInfo>();
            }
        }

        private static string TruncateButton(string text, int max)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= max ? text : text[..(max - 1)] + "…";
        }

        private async Task ReplyEngineMenuAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            var engine = AgentFacade.GetActiveEngine();
            var cursorLabel = (engine == AgentEngineKind.Cursor ? "✅ " : "") + "Cursor";
            var codexLabel = (engine == AgentEngineKind.Codex ? "✅ " : "") + "Codex";
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.enginePick", AgentFacade.DisplayName(engine))),
                TelegramMarkup.Inline(
                    new[]
                    {
                        (cursorLabel, CallbackEngineCursor),
                        (codexLabel, CallbackEngineCodex)
                    }),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyPreviewAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            var html = await TelegramDeployPreview.BuildHtmlAsync(active).ConfigureAwait(false);
            const int maxLen = 3500;
            if (html.Length <= maxLen)
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    html,
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            // Split at </pre> if needed — send truncated list with note.
            var truncated = html[..maxLen] + "…</pre>\n" + TelegramMarkup.Html(Loc.T("telegram.previewTruncated"));
            await SendBotAsync(
                token,
                update.ChatId,
                truncated,
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
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

            if (string.Equals(data, CallbackWorkspaceRoots, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyWorkspaceRootsAsync(token, update, cancellationToken).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(data, CallbackStopTurn, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyStopAgentAsync(token, update, cancellationToken).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(
                    token,
                    update.CallbackQueryId,
                    Loc.T("telegram.agentStopping"),
                    cancellationToken).ConfigureAwait(false);
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
                AgentFacade.CancelCurrentTurn(active);
                AgentFacade.RestartProjectAgent(active);
                await _client.AnswerCallbackQueryAsync(
                    token,
                    update.CallbackQueryId,
                    Loc.T("cursor.restarted"),
                    cancellationToken).ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("cursor.restarted")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
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

            if (string.Equals(data, CallbackEngineMenu, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyEngineMenuAsync(token, update, cancellationToken).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(data, CallbackEngineCursor, StringComparison.OrdinalIgnoreCase)
                || string.Equals(data, CallbackEngineCodex, StringComparison.OrdinalIgnoreCase))
            {
                var next = string.Equals(data, CallbackEngineCodex, StringComparison.OrdinalIgnoreCase)
                    ? AgentEngineKind.Codex
                    : AgentEngineKind.Cursor;
                AgentFacade.SwitchEngine(active, next);
                var msg = Loc.T("telegram.engineSwitched", AgentFacade.DisplayName(next));
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, msg, cancellationToken)
                    .ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(msg),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            if (data.StartsWith(CallbackModelFamilyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var family = data[CallbackModelFamilyPrefix.Length..].Trim().ToLowerInvariant();
                var all = await GetCursorModelsBoundedAsync(forceRefresh: false, cancellationToken)
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
                AgentFacade.SetModelAndRestart(active, model);
                var msg = Loc.T("cursor.modelSet", model);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, msg, cancellationToken)
                    .ConfigureAwait(false);
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(msg),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            if (!TelegramProjectProfile.SupportsDeploy(active))
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("telegram.deployNotAvailable", TelegramProjectProfile.GetKindLabel(active))),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            TelegramProjectSync.ApplyToGitDeploy(active);

            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(Loc.T("telegram.deployStarting")),
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
                cancellationToken,
                active).ConfigureAwait(false);

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
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private async Task ReplyCursorCacheAsync(
            string token,
            TelegramIncomingUpdate update,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);
            var scan = GitDeployPro.Services.Cursor.CursorDiskCleanupService.Scan();
            var html = BuildCursorCacheHtml(scan);
            var markup = BuildCursorCacheKeyboard(scan.CursorRunning);
            await SendBotAsync(token, update.ChatId, html, markup, cancellationToken, active)
                .ConfigureAwait(false);
        }

        private async Task HandleCursorCacheCallbackAsync(
            string token,
            TelegramIncomingUpdate update,
            string data,
            CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var active = ResolveInboundProjectPath(config);

            if (string.Equals(data, CallbackCursorCache, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyCursorCacheAsync(token, update, cancellationToken).ConfigureAwait(false);
                await _client.AnswerCallbackQueryAsync(token, update.CallbackQueryId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var force = data.EndsWith(":force", StringComparison.OrdinalIgnoreCase);
            var aggressive = data.Contains("agg", StringComparison.OrdinalIgnoreCase);
            var profile = aggressive
                ? GitDeployPro.Services.Cursor.CursorCleanupProfile.Aggressive
                : GitDeployPro.Services.Cursor.CursorCleanupProfile.Safe;

            await _client.AnswerCallbackQueryAsync(
                    token,
                    update.CallbackQueryId,
                    Loc.T("telegram.cursorCacheWorking"),
                    cancellationToken)
                .ConfigureAwait(false);

            var result = await GitDeployPro.Services.Cursor.CursorDiskCleanupService
                .ClearAsync(profile, force, cancellationToken)
                .ConfigureAwait(false);

            if (result.AbortedBecauseRunning && !force)
            {
                var warn = TelegramMarkup.Html(Loc.T("telegram.cursorCacheNeedQuit")) + "\n" +
                           TelegramMarkup.Html(result.Message);
                var forceCb = aggressive ? CallbackCursorCacheAggForce : CallbackCursorCacheSafeForce;
                var markup = TelegramMarkup.Inline(
                    new[]
                    {
                        (Loc.T("telegram.kbCursorCacheForceQuit"), forceCb)
                    },
                    new[]
                    {
                        (Loc.T("telegram.kbCursorCacheCancel"), CallbackCursorCache)
                    });
                await SendBotAsync(token, update.ChatId, warn, markup, cancellationToken, active)
                    .ConfigureAwait(false);
                return;
            }

            var done = $"<b>{TelegramMarkup.Html(Loc.T("telegram.cursorCacheDone"))}</b>\n" +
                       TelegramMarkup.Html(result.Message);
            var scan = GitDeployPro.Services.Cursor.CursorDiskCleanupService.Scan();
            done += "\n\n" + BuildCursorCacheHtml(scan);
            await SendBotAsync(
                    token,
                    update.ChatId,
                    done,
                    BuildCursorCacheKeyboard(scan.CursorRunning),
                    cancellationToken,
                    active)
                .ConfigureAwait(false);
        }

        private static string BuildCursorCacheHtml(GitDeployPro.Services.Cursor.CursorDiskScanResult scan)
        {
            var fmt = GitDeployPro.Services.Cursor.CursorDiskCleanupService.FormatBytes;
            var sb = new StringBuilder();
            sb.Append("<b>").Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheTitle"))).Append("</b>\n");
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheTotal", fmt(scan.TotalCursorBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheState", fmt(scan.StateDbBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheBackup", fmt(scan.StateBackupBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheAgent", fmt(scan.AgentWorkerBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheCaches", fmt(scan.CacheBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T(
                "telegram.cursorCacheSafeEst",
                fmt(scan.SafeReclaimableBytes)))).Append('\n');
            sb.Append(TelegramMarkup.Html(Loc.T(
                "telegram.cursorCacheAggEst",
                fmt(scan.AggressiveReclaimableBytes)))).Append('\n');
            if (scan.CursorRunning)
            {
                sb.Append("⚠️ ").Append(TelegramMarkup.Html(Loc.T(
                    "telegram.cursorCacheRunning",
                    string.Join(", ", scan.RunningProcessNames)))).Append('\n');
            }

            sb.Append('\n').Append(TelegramMarkup.Html(Loc.T("telegram.cursorCacheHint")));
            return sb.ToString().TrimEnd();
        }

        private static JObject BuildCursorCacheKeyboard(bool cursorRunning)
        {
            if (cursorRunning)
            {
                return TelegramMarkup.Inline(
                    new[]
                    {
                        (Loc.T("telegram.kbCursorCacheSafe"), CallbackCursorCacheSafe),
                        (Loc.T("telegram.kbCursorCacheAgg"), CallbackCursorCacheAgg)
                    },
                    new[]
                    {
                        (Loc.T("telegram.kbCursorCacheForceQuit"), CallbackCursorCacheSafeForce)
                    });
            }

            return TelegramMarkup.Inline(new[]
            {
                (Loc.T("telegram.kbCursorCacheSafe"), CallbackCursorCacheSafe),
                (Loc.T("telegram.kbCursorCacheAgg"), CallbackCursorCacheAgg)
            });
        }

        private static string FormatCursorDiskStatusHtml()
        {
            try
            {
                var scan = GitDeployPro.Services.Cursor.CursorDiskCleanupService.Scan();
                var fmt = GitDeployPro.Services.Cursor.CursorDiskCleanupService.FormatBytes;
                return TelegramMarkup.Html(Loc.T(
                    "telegram.statusCursorDisk",
                    fmt(scan.TotalCursorBytes),
                    fmt(scan.SafeReclaimableBytes)));
            }
            catch
            {
                return string.Empty;
            }
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
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
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
            var kindLine = "\n" + TelegramMarkup.Html(Loc.T(
                "telegram.projectKindActive",
                TelegramProjectProfile.GetKindLabel(path)));
            var workspaceLine = "\n" + TelegramMarkup.Html(CursorWorkspaceRoots.FormatAnnouncement(
                CursorWorkspaceRoots.GetRoots(path)));
            var html =
                $"✅ <b>{TelegramMarkup.Html(switched)}</b>" +
                kindLine +
                pendingLine +
                workspaceLine + "\n\n" +
                TelegramMarkup.Html(Loc.T("telegram.homeBody"));
            await SendBotAsync(
                token,
                update.ChatId,
                html,
                TelegramDeployCoordinator.BuildReplyKeyboard(path),
                cancellationToken,
                path).ConfigureAwait(false);

            // Local chat mirror only — Telegram already received roots in the switch reply.
            CursorWorkspaceRoots.AnnounceToChatAndTelegram(path, sendTelegram: false);
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

            // Queue outbound so slow/failed sends never stall the poll loop or sibling handlers.
            TelegramOutboundQueue.Instance.EnqueueText(
                token,
                chatId,
                trackPath ?? string.Empty,
                html ?? string.Empty,
                markup ?? TelegramDeployCoordinator.BuildReplyKeyboard(trackPath),
                "HTML");

            await Task.CompletedTask.ConfigureAwait(false);
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

        private static bool IsPlansCommand(string text)
            => MatchesCommand(
                text,
                "/plans",
                "telegram.kbPlans",
                "plans",
                "پلن‌ها",
                "پلنها",
                "لیست پلن");

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

        private static bool IsPreviewCommand(string text)
            => MatchesCommand(
                text,
                "/preview",
                "telegram.kbPreview",
                "telegram.btnPreview",
                "preview",
                "پیش‌نمایش",
                "پیش نمایش");

        private static bool IsPlanModeCommand(string text)
            => MatchesCommand(
                text,
                "/plan",
                "telegram.kbPlan",
                "telegram.kbPlanOn",
                "telegram.btnPlan",
                "plan",
                "plan mode",
                "پلن",
                "حالت پلن");

        private static bool IsAgentModeCommand(string text)
            => MatchesCommand(
                text,
                "/agent",
                "telegram.kbAgent",
                "telegram.btnAgent",
                "agent",
                "agent mode",
                "ایجنت",
                "حالت ایجنت");

        private async Task ReplySetCursorModeAsync(
            string token,
            TelegramIncomingUpdate update,
            string mode,
            CancellationToken cancellationToken)
        {
            var active = ResolveInboundProjectPath(_config.LoadGlobalConfig());
            if (AgentFacade.GetActiveEngine() != AgentEngineKind.Cursor)
            {
                await SendBotAsync(
                    token,
                    update.ChatId,
                    TelegramMarkup.Html(Loc.T("cursor.modeCursorOnly")),
                    TelegramDeployCoordinator.BuildReplyKeyboard(active),
                    cancellationToken,
                    active).ConfigureAwait(false);
                return;
            }

            var next = string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "agent";
            CursorAgentBridge.Instance.SetAgentModeAndRestart(active, next);
            var msg = next == "plan" ? Loc.T("cursor.modePlan") : Loc.T("cursor.modeAgent");
            await SendBotAsync(
                token,
                update.ChatId,
                TelegramMarkup.Html(msg),
                TelegramDeployCoordinator.BuildReplyKeyboard(active),
                cancellationToken,
                active).ConfigureAwait(false);
        }

        private static string FormatAccountUsageHtml(CursorAccountUsageSnapshot usage)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.statusPlanUsage"))).Append('\n');
            if (!usage.Ok)
            {
                sb.Append(TelegramMarkup.Html(Loc.T("telegram.statusPlanUsageFail", usage.Error)));
                return sb.ToString();
            }

            sb.Append(TelegramMarkup.Html(Loc.T(
                "telegram.statusPlanUsageLine",
                usage.CursorModelsPercent,
                usage.OtherModelsPercent)));
            return sb.ToString();
        }

        private static string FormatUsageStatusHtml(CursorAgentStatusInfo status)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(TelegramMarkup.Html(Loc.T("telegram.statusUsage"))).Append('\n');
            if (status.LastUsage is { Available: true } last)
            {
                sb.Append(TelegramMarkup.Html(Loc.T(
                    "telegram.statusUsageLast",
                    last.InputTokens,
                    last.OutputTokens,
                    last.CacheReadTokens,
                    last.CacheWriteTokens,
                    last.Source)));
            }
            else
            {
                sb.Append(TelegramMarkup.Html(Loc.T("telegram.statusUsageLastNa")));
            }

            sb.Append('\n');
            if (status.SessionUsage is { Available: true } session)
            {
                sb.Append(TelegramMarkup.Html(Loc.T(
                    "telegram.statusUsageSession",
                    session.InputTokens,
                    session.OutputTokens,
                    session.CacheReadTokens,
                    session.CacheWriteTokens)));
            }
            else
            {
                sb.Append(TelegramMarkup.Html(Loc.T("telegram.statusUsageSessionNa")));
            }

            return sb.ToString();
        }

        private static bool IsEngineCommand(string text)
            => MatchesCommand(
                text,
                "/engine",
                "telegram.kbEngine",
                "telegram.btnEngine",
                "engine",
                "موتور",
                "ایجنت");

        private static bool IsClearLocalCommand(string text)
            => MatchesCommand(
                text,
                "/clear",
                "telegram.kbClear",
                "telegram.btnClear",
                "clear",
                "پاک کردن چت",
                "پاک کردن");

        private static bool IsCursorCacheCommand(string text)
            => MatchesCommand(
                   text,
                   "/cursorcache",
                   "telegram.kbCursorCache",
                   "cursorcache",
                   "cursor cache",
                   "کش کورسر",
                   "کش کرسر",
                   "پاکسازی کرسر")
               || MatchesCommand(text, "/cursorclean", "cursorclean");

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

        private static bool IsStopAgentCommand(string text)
            => MatchesCommand(
                text,
                "/stop",
                "telegram.btnStopAgent",
                "stop",
                "توقف",
                "استاپ",
                "بایست");

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
                .Replace("👁", "")
                .Replace("⚙️", "")
                .Trim();
            return chars;
        }
    }
}
