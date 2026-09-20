using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Plan-mode buttons: Get MD / Build, bound to a specific plan file.
    /// Callbacks (durable across restart via thread PlanCallbackMap):
    ///   pmd:f:{8hex} / pbld:f:{8hex}  → exact plan for that message
    ///   pmd:last / pbld:last          → newest plan on disk
    ///   pmd:i:N / pbld:i:N            → Nth in /plans list
    /// </summary>
    internal sealed class TelegramPlanMdBroker
    {
        public static TelegramPlanMdBroker Instance { get; } = new();

        public const string CallbackPrefix = "pmd:";
        public const string CallbackBuildPrefix = "pbld:";
        public const string CallbackDeletePrefix = "pldel:";
        public const string LastToken = "last";

        /// <summary>
        /// Registers THIS plan file and returns a durable callback id (f:xxxxxxxx).
        /// Returns null when there is no real plan file — caller must not show Get MD/Build.
        /// </summary>
        public string? Register(string projectPath, string? preferredFilePath)
        {
            if (string.IsNullOrWhiteSpace(preferredFilePath) || !File.Exists(preferredFilePath))
            {
                return null;
            }

            if (!CursorPlanCatalog.IsUnderPlansDir(projectPath, preferredFilePath))
            {
                return null;
            }

            CursorPlanFileWriter.EnsurePlansDirectory(projectPath);
            var id = TelegramChatStore.Instance.RegisterPlanCallback(projectPath, preferredFilePath);
            return string.IsNullOrWhiteSpace(id) ? null : "f:" + id;
        }

        public bool IsPlanMdCallback(string? callbackData)
            => (callbackData ?? string.Empty).StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase);

        public bool IsBuildCallback(string? callbackData)
            => (callbackData ?? string.Empty).StartsWith(CallbackBuildPrefix, StringComparison.OrdinalIgnoreCase);

        public bool IsDeleteCallback(string? callbackData)
            => (callbackData ?? string.Empty).StartsWith(CallbackDeletePrefix, StringComparison.OrdinalIgnoreCase);

        public static JObject BuildDocumentKeyboard(string pendingId)
            => TelegramMarkup.Inline(new[]
            {
                (Loc.T("telegram.kbBuildPlan"), CallbackBuildPrefix + pendingId)
            });

        public static JObject BuildCardKeyboard(string pendingId)
            => TelegramMarkup.Inline(new[]
            {
                (Loc.T("telegram.kbGetPlanMd"), CallbackPrefix + pendingId),
                (Loc.T("telegram.kbBuildPlan"), CallbackBuildPrefix + pendingId)
            });

        public async Task<(bool Handled, string? Ack)> TryHandleAsync(
            string token,
            long chatId,
            string callbackData,
            string? messageText,
            CancellationToken cancellationToken)
        {
            var data = (callbackData ?? string.Empty).Trim();
            if (!data.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            var key = data[CallbackPrefix.Length..].Trim();
            var projectPath = ResolveProjectPath();
            var path = ResolvePlanPath(projectPath, key, messageText);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return (true, Loc.T("telegram.planMdNotFound"));
            }

            try
            {
                var boundId = Register(projectPath, path) ?? LastToken;
                var caption = Loc.T("cursor.planDocumentCaption", Path.GetFileName(path));
                TelegramOutboundQueue.Instance.EnqueueDocument(
                    token,
                    chatId,
                    projectPath,
                    path,
                    caption,
                    BuildDocumentKeyboard(boundId));

                var note = new TelegramChatMessage
                {
                    Direction = TelegramMessageDirection.System,
                    Status = TelegramMessageStatus.Sent,
                    Text = Loc.T("cursor.planSentTelegram", Path.GetFileName(path)),
                    Utc = DateTime.UtcNow,
                    SenderName = "Cursor",
                    AttachmentPath = path,
                    AttachmentName = Path.GetFileName(path)
                };
                TelegramChatStore.Instance.Append(projectPath, note);
                TelegramPoller.Instance.RaiseMessage(projectPath, note);

                await Task.CompletedTask.ConfigureAwait(false);
                return (true, Loc.T("telegram.planMdSending"));
            }
            catch (Exception ex)
            {
                return (true, Loc.T("telegram.planMdFailed", Truncate(ex.Message, 80)));
            }
        }

        public async Task<(bool Handled, string? Ack)> TryHandleBuildAsync(
            string token,
            long chatId,
            string callbackData,
            string? messageText,
            CancellationToken cancellationToken)
        {
            var data = (callbackData ?? string.Empty).Trim();
            if (!data.StartsWith(CallbackBuildPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            var key = data[CallbackBuildPrefix.Length..].Trim();
            var projectPath = ResolveProjectPath();
            var path = ResolvePlanPath(projectPath, key, messageText);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return (true, Loc.T("telegram.planBuildNotFound"));
            }

            try
            {
                if (AgentFacade.GetActiveEngine() != AgentEngineKind.Cursor)
                {
                    return (true, Loc.T("cursor.modeCursorOnly"));
                }

                CursorPlanCatalog.Remember(projectPath, path);
                CursorAgentBridge.Instance.SetAgentModeAndRestart(
                    projectPath,
                    "agent",
                    Loc.T("telegram.planBuildStarted"));

                AgentFacade.EnqueueUserTurn(projectPath, BuildImplementPrompt(path), null);

                TelegramOutboundQueue.Instance.EnqueueText(
                    token,
                    chatId,
                    projectPath,
                    TelegramMarkup.Html(Loc.T("telegram.planBuildQueued", Path.GetFileName(path))),
                    TelegramDeployCoordinator.BuildReplyKeyboard(projectPath),
                    parseMode: "HTML");

                await Task.CompletedTask.ConfigureAwait(false);
                return (true, Loc.T("telegram.planBuildStarted"));
            }
            catch (Exception ex)
            {
                return (true, Loc.T("telegram.planBuildFailed", Truncate(ex.Message, 80)));
            }
        }

        public Task<(bool Handled, string? Ack)> TryHandleDeleteAsync(
            string token,
            long chatId,
            string callbackData,
            CancellationToken cancellationToken)
        {
            var data = (callbackData ?? string.Empty).Trim();
            if (!data.StartsWith(CallbackDeletePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<(bool, string?)>((false, null));
            }

            var key = data[CallbackDeletePrefix.Length..].Trim();
            var projectPath = ResolveProjectPath();

            if (string.Equals(key, "all", StringComparison.OrdinalIgnoreCase))
            {
                var n = CursorPlanCatalog.DeleteAll(projectPath);
                TelegramOutboundQueue.Instance.EnqueueText(
                    token,
                    chatId,
                    projectPath,
                    TelegramMarkup.Html(Loc.T("telegram.plansDeletedAll", n)),
                    TelegramDeployCoordinator.BuildReplyKeyboard(projectPath),
                    parseMode: "HTML");
                return Task.FromResult<(bool, string?)>((true, Loc.T("telegram.plansDeletedAck", n)));
            }

            var path = ResolvePlanPath(projectPath, key, messageText: null);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Task.FromResult<(bool, string?)>((true, Loc.T("telegram.planMdNotFound")));
            }

            var name = Path.GetFileName(path);
            if (!CursorPlanCatalog.TryDelete(projectPath, path, out var err))
            {
                return Task.FromResult<(bool, string?)>((true, Loc.T("telegram.plansDeleteFailed", err ?? "?")));
            }

            TelegramOutboundQueue.Instance.EnqueueText(
                token,
                chatId,
                projectPath,
                TelegramMarkup.Html(Loc.T("telegram.plansDeletedOne", name)),
                TelegramDeployCoordinator.BuildReplyKeyboard(projectPath),
                parseMode: "HTML");
            return Task.FromResult<(bool, string?)>((true, Loc.T("telegram.plansDeletedOneAck", name)));
        }

        private static string ResolveProjectPath()
        {
            var active = TelegramChatStore.Instance.GetActiveProjectPath();
            return string.IsNullOrWhiteSpace(active) ? TelegramPaths.UnassignedPath : active;
        }

        private static string? ResolvePlanPath(string projectPath, string key, string? messageText)
        {
            key = (key ?? string.Empty).Trim();

            if (key.StartsWith("f:", StringComparison.OrdinalIgnoreCase))
            {
                var id = key[2..].Trim();
                var mapped = TelegramChatStore.Instance.ResolvePlanCallback(projectPath, id);
                if (!string.IsNullOrWhiteSpace(mapped) && File.Exists(mapped))
                {
                    return mapped;
                }

                var byName = CursorPlanCatalog.ResolveByFileName(projectPath, id);
                if (!string.IsNullOrWhiteSpace(byName))
                {
                    return byName;
                }
            }

            if (key.StartsWith("i:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key[2..], out var index))
            {
                var byIndex = CursorPlanCatalog.GetByIndex(projectPath, index);
                if (!string.IsNullOrWhiteSpace(byIndex))
                {
                    return byIndex;
                }
            }

            if (!string.IsNullOrWhiteSpace(messageText))
            {
                var scraped = TelegramDocumentDispatch.CollectPaths(projectPath, messageText, messageText);
                foreach (var candidate in scraped)
                {
                    if (CursorPlanCatalog.IsUnderPlansDir(projectPath, candidate))
                    {
                        return candidate;
                    }
                }
            }

            // last / legacy → newest by date on disk
            return CursorPlanCatalog.GetLatest(projectPath);
        }

        private static string BuildImplementPrompt(string planFilePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Build / implement this plan now (same as Cursor IDE Build on a plan).");
            sb.AppendLine("Read the plan file carefully, then execute it end-to-end in the workspace.");
            sb.AppendLine("Do not re-plan. Do not ask what to do. Start implementing immediately.");
            sb.AppendLine($"Plan file (absolute path): {planFilePath}");
            sb.AppendLine("When done, reply with a short result of what changed and what to test.");
            return sb.ToString().Trim();
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
