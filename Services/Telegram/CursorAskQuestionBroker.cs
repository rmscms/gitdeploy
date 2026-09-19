using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Bridges ACP <c>cursor/ask_question</c> to Telegram inline keyboards and waits for a callback answer.
    /// </summary>
    internal sealed class CursorAskQuestionBroker
    {
        public static CursorAskQuestionBroker Instance { get; } = new();

        public const string CallbackPrefix = "aq:";

        private readonly ConcurrentDictionary<string, PendingAsk> _pending = new(StringComparer.Ordinal);

        public async Task<JObject?> AskAsync(string projectPath, JObject parameters, TimeSpan timeout)
        {
            var questions = parameters["questions"] as JArray
                            ?? parameters["items"] as JArray
                            ?? new JArray();
            if (questions.Count == 0)
            {
                // Single-question shape
                if (parameters["prompt"] != null || parameters["question"] != null)
                {
                    questions.Add(parameters);
                }
            }

            if (questions.Count == 0)
            {
                return null;
            }

            var q0 = questions[0] as JObject ?? new JObject();
            var prompt = (q0["prompt"]?.ToString()
                          ?? q0["question"]?.ToString()
                          ?? q0["text"]?.ToString()
                          ?? Loc.T("cursor.askQuestionFallback")).Trim();
            var options = ParseOptions(q0);
            if (options.Count == 0)
            {
                options.Add(("yes", Loc.T("cursor.askYes")));
                options.Add(("no", Loc.T("cursor.askNo")));
            }

            var askId = Guid.NewGuid().ToString("N")[..10];
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[askId] = new PendingAsk(tcs, options);

            try
            {
                await SendQuestionToTelegramAsync(projectPath, askId, prompt, options).ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(askId, out _);
                return null;
            }

            using var cts = new CancellationTokenSource(timeout);
            await using var reg = cts.Token.Register(() => tcs.TrySetResult(null!));
            var result = await tcs.Task.ConfigureAwait(false);
            _pending.TryRemove(askId, out _);
            return result;
        }

        public bool TryAnswer(string callbackData, out string? ackMessage)
        {
            ackMessage = null;
            var data = (callbackData ?? string.Empty).Trim();
            if (!data.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = data[CallbackPrefix.Length..];
            var parts = rest.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            var askId = parts[0];
            if (!int.TryParse(parts[1], out var optionIndex))
            {
                return false;
            }

            if (!_pending.TryGetValue(askId, out var pending))
            {
                ackMessage = Loc.T("cursor.askExpired");
                return true;
            }

            if (optionIndex < 0 || optionIndex >= pending.Options.Count)
            {
                ackMessage = Loc.T("cursor.askInvalid");
                return true;
            }

            var (optId, label) = pending.Options[optionIndex];
            var outcome = new JObject
            {
                ["outcome"] = new JObject
                {
                    ["outcome"] = "selected",
                    ["optionId"] = optId,
                    ["answers"] = new JArray
                    {
                        new JObject
                        {
                            ["questionId"] = askId,
                            ["selectedOptionIds"] = new JArray(optId)
                        }
                    }
                }
            };

            pending.Completion.TrySetResult(outcome);
            ackMessage = Loc.T("cursor.askPicked", label);
            return true;
        }

        private static List<(string Id, string Label)> ParseOptions(JObject question)
        {
            var list = new List<(string Id, string Label)>();
            var arr = question["options"] as JArray ?? question["choices"] as JArray;
            if (arr == null)
            {
                return list;
            }

            var i = 0;
            foreach (var item in arr.OfType<JObject>())
            {
                var id = (item["id"]?.ToString()
                          ?? item["value"]?.ToString()
                          ?? ("opt" + i)).Trim();
                var label = (item["label"]?.ToString()
                             ?? item["text"]?.ToString()
                             ?? item["title"]?.ToString()
                             ?? id).Trim();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add((id, string.IsNullOrWhiteSpace(label) ? id : label));
                }

                i++;
            }

            return list;
        }

        private static async Task SendQuestionToTelegramAsync(
            string projectPath,
            string askId,
            string prompt,
            IReadOnlyList<(string Id, string Label)> options)
        {
            var config = new ConfigurationService().LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            var chatId = TelegramChatStore.Instance.GetLastChatId();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
            {
                throw new InvalidOperationException("No Telegram chat for ask_question.");
            }

            var rows = new List<(string Text, string Data)[]>();
            for (var i = 0; i < options.Count && i < 12; i++)
            {
                var label = Truncate(options[i].Label, 40);
                rows.Add(new[] { (label, CallbackPrefix + askId + ":" + i) });
            }

            var html = "<b>" + TelegramMarkup.Html(Loc.T("cursor.askTitle")) + "</b>\n"
                       + TelegramMarkup.Html(prompt);
            TelegramOutboundQueue.Instance.EnqueueText(
                token,
                chatId,
                projectPath,
                html,
                TelegramMarkup.Inline(rows.ToArray()),
                "HTML");

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static string Truncate(string text, int max)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= max ? text : text[..(max - 1)] + "…";
        }

        private sealed class PendingAsk
        {
            public PendingAsk(TaskCompletionSource<JObject> completion, List<(string Id, string Label)> options)
            {
                Completion = completion;
                Options = options;
            }

            public TaskCompletionSource<JObject> Completion { get; }
            public List<(string Id, string Label)> Options { get; }
        }
    }
}
