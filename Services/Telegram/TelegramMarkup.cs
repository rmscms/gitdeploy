using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    public static class TelegramMarkup
    {
        public static JObject Inline(params (string Text, string Data)[][] rows)
        {
            var keyboard = new JArray();
            foreach (var row in rows)
            {
                var jrow = new JArray();
                foreach (var (text, data) in row)
                {
                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(data))
                    {
                        continue;
                    }

                    jrow.Add(new JObject
                    {
                        ["text"] = Truncate(text, 64),
                        ["callback_data"] = Truncate(data, 64)
                    });
                }

                if (jrow.Count > 0)
                {
                    keyboard.Add(jrow);
                }
            }

            return new JObject { ["inline_keyboard"] = keyboard };
        }

        public static JObject InlineRows(IEnumerable<IReadOnlyList<(string Text, string Data)>> rows)
        {
            var keyboard = new JArray();
            foreach (var row in rows)
            {
                var jrow = new JArray();
                foreach (var (text, data) in row)
                {
                    jrow.Add(new JObject
                    {
                        ["text"] = Truncate(text, 64),
                        ["callback_data"] = Truncate(data, 64)
                    });
                }

                if (jrow.Count > 0)
                {
                    keyboard.Add(jrow);
                }
            }

            return new JObject { ["inline_keyboard"] = keyboard };
        }

        public static JObject ReplyKeyboard(params string[][] rows)
        {
            var keyboard = new JArray();
            foreach (var row in rows)
            {
                var jrow = new JArray();
                foreach (var text in row)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        jrow.Add(new JObject { ["text"] = text });
                    }
                }

                if (jrow.Count > 0)
                {
                    keyboard.Add(jrow);
                }
            }

            return new JObject
            {
                ["keyboard"] = keyboard,
                ["resize_keyboard"] = true,
                ["is_persistent"] = true
            };
        }

        /// <summary>
        /// Clears a stuck Telegram reply keyboard so the next markup can replace it.
        /// </summary>
        public static JObject RemoveReplyKeyboard()
        {
            return new JObject { ["remove_keyboard"] = true };
        }

        public static string Html(string? value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value;
            }

            return value[..max];
        }
    }
}
