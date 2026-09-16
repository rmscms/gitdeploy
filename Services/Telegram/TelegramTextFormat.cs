using System;
using System.Text;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// RTL detection for chat bubbles + markdown → Telegram HTML for bot replies.
    /// </summary>
    public static class TelegramTextFormat
    {
        private static readonly Regex BoldMd = new(@"\*\*(.+?)\*\*", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ItalicMd = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex CodeBlockMd = new(@"```(?:\w+)?\s*([\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex InlineCodeMd = new(@"`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex AlreadyHtml = new(@"</?(b|i|u|s|code|pre|a)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsMostlyRtl(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var rtl = 0;
            var ltr = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch) || char.IsDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                {
                    continue;
                }

                if (IsRtlChar(ch))
                {
                    rtl++;
                }
                else if (char.IsLetter(ch))
                {
                    ltr++;
                }
            }

            if (rtl == 0 && ltr == 0)
            {
                return false;
            }

            return rtl >= ltr;
        }

        public static System.Windows.FlowDirection DetectFlow(string? text)
            => IsMostlyRtl(text)
                ? System.Windows.FlowDirection.RightToLeft
                : System.Windows.FlowDirection.LeftToRight;

        public static System.Windows.TextAlignment DetectAlignment(string? text)
            => IsMostlyRtl(text)
                ? System.Windows.TextAlignment.Right
                : System.Windows.TextAlignment.Left;

        /// <summary>
        /// Converts agent markdown (or plain text) into Telegram HTML. Escapes unsafe characters.
        /// If the text already looks like Telegram HTML, escapes only raw &lt;/&gt;/&amp; outside tags carefully via WebUtility after light sanitize.
        /// </summary>
        public static string ToTelegramHtml(string? text)
        {
            var raw = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            if (AlreadyHtml.IsMatch(raw))
            {
                // Agent already used HTML — escape bare &lt; that aren't tags is hard; trust Telegram tags and escape & only when not entity.
                return SanitizeLooseHtml(raw);
            }

            // Protect code blocks first.
            var parts = new System.Collections.Generic.List<string>();
            string Protect(string htmlSnippet)
            {
                var token = $"\uE000{parts.Count}\uE001";
                parts.Add(htmlSnippet);
                return token;
            }

            raw = CodeBlockMd.Replace(raw, m =>
                Protect($"<pre>{TelegramMarkup.Html(m.Groups[1].Value.TrimEnd())}</pre>"));

            raw = InlineCodeMd.Replace(raw, m =>
                Protect($"<code>{TelegramMarkup.Html(m.Groups[1].Value)}</code>"));

            raw = BoldMd.Replace(raw, m =>
                Protect($"<b>{TelegramMarkup.Html(m.Groups[1].Value)}</b>"));

            raw = ItalicMd.Replace(raw, m =>
                Protect($"<i>{TelegramMarkup.Html(m.Groups[1].Value)}</i>"));

            // Escape remaining plain text.
            raw = TelegramMarkup.Html(raw);

            for (var i = 0; i < parts.Count; i++)
            {
                raw = raw.Replace($"\uE000{i}\uE001", parts[i]);
            }

            return raw;
        }

        private static string SanitizeLooseHtml(string html)
        {
            // Escape & that are not already entities.
            var sb = new StringBuilder(html.Length + 16);
            for (var i = 0; i < html.Length; i++)
            {
                var c = html[i];
                if (c == '&')
                {
                    var rest = html.AsSpan(i);
                    if (rest.StartsWith("&amp;", StringComparison.OrdinalIgnoreCase)
                        || rest.StartsWith("&lt;", StringComparison.OrdinalIgnoreCase)
                        || rest.StartsWith("&gt;", StringComparison.OrdinalIgnoreCase)
                        || rest.StartsWith("&quot;", StringComparison.OrdinalIgnoreCase)
                        || rest.StartsWith("&#", StringComparison.Ordinal))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append("&amp;");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static bool IsRtlChar(char ch)
        {
            // Arabic, Persian, Hebrew blocks.
            return (ch >= '\u0590' && ch <= '\u05FF')
                   || (ch >= '\u0600' && ch <= '\u06FF')
                   || (ch >= '\u0750' && ch <= '\u077F')
                   || (ch >= '\u08A0' && ch <= '\u08FF')
                   || (ch >= '\uFB50' && ch <= '\uFDFF')
                   || (ch >= '\uFE70' && ch <= '\uFEFF');
        }
    }
}
