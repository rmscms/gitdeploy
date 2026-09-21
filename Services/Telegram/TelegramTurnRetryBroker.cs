using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// When Cursor hits a capacity/rate-limit error, stores the failed turn and offers Telegram Resume buttons.
    /// Callbacks: rt:s:{id} same model · rt:a:{id} auto · rt:n:{id}:{index} alternate model.
    /// </summary>
    internal sealed class TelegramTurnRetryBroker
    {
        public static TelegramTurnRetryBroker Instance { get; } = new();

        public const string CallbackPrefix = "rt:";

        public bool IsRetryCallback(string? callbackData)
            => (callbackData ?? string.Empty).StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase);

        public string Register(
            string projectPath,
            string userText,
            string? photoPath,
            string modelAtFailure,
            IReadOnlyList<string> altModels)
        {
            return TelegramChatStore.Instance.RegisterTurnRetry(
                projectPath,
                userText,
                photoPath,
                modelAtFailure,
                altModels);
        }

        public static JObject BuildKeyboard(string retryId, IReadOnlyList<string> altModels)
        {
            var rows = new List<(string Text, string Data)[]>
            {
                new[]
                {
                    (Loc.T("telegram.kbRetrySame"), CallbackPrefix + "s:" + retryId),
                    (Loc.T("telegram.kbRetryAuto"), CallbackPrefix + "a:" + retryId)
                }
            };

            var pair = new List<(string Text, string Data)>();
            for (var i = 0; i < Math.Min(altModels.Count, 4); i++)
            {
                var model = altModels[i];
                var label = Loc.T(
                    "telegram.kbRetryModel",
                    CursorAcpErrorHelper.ShortModelLabel(model));
                var data = CallbackPrefix + "n:" + retryId + ":" + i;
                if (data.Length > 64)
                {
                    continue;
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

            return TelegramMarkup.Inline(rows.ToArray());
        }

        public async Task<(bool Handled, string? Ack)> TryHandleAsync(
            string token,
            long chatId,
            string callbackData,
            CancellationToken cancellationToken)
        {
            _ = token;
            _ = chatId;
            _ = cancellationToken;

            var data = (callbackData ?? string.Empty).Trim();
            if (!data.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            var rest = data[CallbackPrefix.Length..];
            string mode;
            string id;
            int altIndex = -1;

            if (rest.StartsWith("s:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "same";
                id = rest[2..];
            }
            else if (rest.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "auto";
                id = rest[2..];
            }
            else if (rest.StartsWith("n:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "alt";
                var parts = rest[2..].Split(':', 2);
                if (parts.Length != 2 || !int.TryParse(parts[1], out altIndex))
                {
                    return (true, Loc.T("cursor.retryInvalid"));
                }

                id = parts[0];
            }
            else
            {
                return (true, Loc.T("cursor.retryInvalid"));
            }

            var projectPath = TelegramChatStore.Instance.GetActiveProjectPath();
            if (TelegramPaths.IsUnassigned(projectPath))
            {
                return (true, Loc.T("telegram.noProject"));
            }

            var entry = TelegramChatStore.Instance.ResolveTurnRetry(projectPath, id);
            if (entry == null)
            {
                return (true, Loc.T("cursor.retryExpired"));
            }

            var modelSwitch = entry.ModelAtFailure;
            if (mode == "auto")
            {
                modelSwitch = "auto";
            }
            else if (mode == "alt")
            {
                if (altIndex < 0 || altIndex >= entry.AltModels.Count)
                {
                    return (true, Loc.T("cursor.retryInvalid"));
                }

                modelSwitch = entry.AltModels[altIndex];
            }

            if (mode == "auto" || mode == "alt")
            {
                var reason = mode == "auto"
                    ? Loc.T("cursor.retryQueuedAuto")
                    : Loc.T("cursor.retryQueuedModel", CursorAcpErrorHelper.ShortModelLabel(modelSwitch));
                CursorAgentBridge.Instance.SwitchModelKeepMode(projectPath, modelSwitch, reason);
            }
            else
            {
                AgentFacade.RestartProjectAgent(projectPath, Loc.T("cursor.retryRestart"));
            }

            var photo = !string.IsNullOrWhiteSpace(entry.PhotoPath) && File.Exists(entry.PhotoPath)
                ? entry.PhotoPath
                : null;

            AgentFacade.EnqueueUserTurn(projectPath, entry.Text, photo);

            var ack = mode switch
            {
                "auto" => Loc.T("cursor.retryQueuedAuto"),
                "alt" => Loc.T("cursor.retryQueuedModel", CursorAcpErrorHelper.ShortModelLabel(modelSwitch)),
                _ => Loc.T("cursor.retryQueued")
            };

            return (true, ack);
        }
    }
}
