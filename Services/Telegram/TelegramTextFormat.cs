using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// RTL detection for chat bubbles + markdown → Telegram HTML for bot replies.
    /// Plan files: Telegram-compatible Markdown (no HTML tags, RTL marks for Persian).
    /// </summary>
    public static class TelegramTextFormat
    {
        private const char Rlm = '\u200F'; // RIGHT-TO-LEFT MARK
        private const char Rle = '\u202B'; // RIGHT-TO-LEFT EMBEDDING
        private const char Pdf = '\u202C'; // POP DIRECTIONAL FORMATTING

        private static readonly Regex BoldMd = new(@"\*\*(.+?)\*\*", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ItalicMd = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex CodeBlockMd = new(@"```(?:\w+)?\s*([\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex InlineCodeMd = new(@"`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex AlreadyHtml = new(@"</?(b|i|u|s|code|pre|a|strong|em)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HeadingMd = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex TableRowLine = new(@"^\s*\|.+\|\s*$", RegexOptions.Compiled);
        private static readonly Regex TableSepLine = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$", RegexOptions.Compiled);

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
        /// Converts agent markdown (or plain text) into Telegram HTML.
        /// Normalizes HTML→MD first, converts tables, applies RTL marks for Persian.
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
                raw = HtmlToMarkdown(raw);
            }

            raw = ConvertMarkdownTablesToLists(raw);

            var parts = new List<string>();
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

            // Headings → bold lines (Telegram HTML has no h1–h6).
            raw = Regex.Replace(
                raw,
                @"^#{1,6}\s+(.+)$",
                m => Protect($"<b>{TelegramMarkup.Html(m.Groups[1].Value.Trim())}</b>"),
                RegexOptions.Multiline);

            raw = TelegramMarkup.Html(raw);

            for (var i = 0; i < parts.Count; i++)
            {
                raw = raw.Replace($"\uE000{i}\uE001", parts[i]);
            }

            return ApplyRtlToTelegramHtml(raw);
        }

        /// <summary>
        /// Clean Markdown for plan .md files opened in Telegram:
        /// no HTML tags, tables → lists, RTL marks on Persian lines.
        /// </summary>
        public static string ToTelegramCompatibleMarkdown(string? text)
        {
            var md = HtmlToMarkdown(text);
            md = ConvertMarkdownTablesToLists(md);
            md = ApplyRtlToMarkdown(md);
            md = Regex.Replace(md, @"\n{3,}", "\n\n");
            return md.Trim();
        }

        /// <summary>
        /// Converts Telegram HTML emphasis into MD and strips leftover tags.
        /// </summary>
        public static string HtmlToMarkdown(string? text)
        {
            var raw = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            if (!AlreadyHtml.IsMatch(raw))
            {
                return StripControlMarks(raw).Trim();
            }

            raw = Regex.Replace(
                raw,
                @"<pre(?:\s[^>]*)?>([\s\S]*?)</pre>",
                m => "```\n" + DecodeEntities(StripTags(m.Groups[1].Value)).TrimEnd() + "\n```",
                RegexOptions.IgnoreCase);

            raw = Regex.Replace(
                raw,
                @"<code(?:\s[^>]*)?>([\s\S]*?)</code>",
                m => "`" + DecodeEntities(StripTags(m.Groups[1].Value)) + "`",
                RegexOptions.IgnoreCase);

            raw = Regex.Replace(
                raw,
                @"<(?:b|strong)(?:\s[^>]*)?>([\s\S]*?)</(?:b|strong)>",
                m => "**" + DecodeEntities(StripTags(m.Groups[1].Value)) + "**",
                RegexOptions.IgnoreCase);

            raw = Regex.Replace(
                raw,
                @"<(?:i|em)(?:\s[^>]*)?>([\s\S]*?)</(?:i|em)>",
                m => "*" + DecodeEntities(StripTags(m.Groups[1].Value)) + "*",
                RegexOptions.IgnoreCase);

            raw = Regex.Replace(
                raw,
                @"<(?:u|s|strike|del)(?:\s[^>]*)?>([\s\S]*?)</(?:u|s|strike|del)>",
                m => DecodeEntities(StripTags(m.Groups[1].Value)),
                RegexOptions.IgnoreCase);

            raw = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            raw = Regex.Replace(raw, @"</?p(?:\s[^>]*)?>", "\n", RegexOptions.IgnoreCase);
            raw = StripTags(raw);
            raw = DecodeEntities(raw);
            raw = Regex.Replace(raw, @"\n{3,}", "\n\n");
            return StripControlMarks(raw).Trim();
        }

        /// <summary>GFM pipe tables → bullet lists (Telegram has no tables).</summary>
        public static string ConvertMarkdownTablesToLists(string? text)
        {
            var raw = (text ?? string.Empty).Replace("\r\n", "\n");
            if (string.IsNullOrWhiteSpace(raw) || !raw.Contains('|'))
            {
                return raw;
            }

            var lines = raw.Split('\n');
            var sb = new StringBuilder(raw.Length);
            var i = 0;
            while (i < lines.Length)
            {
                if (i + 1 < lines.Length
                    && TableRowLine.IsMatch(lines[i])
                    && TableSepLine.IsMatch(lines[i + 1]))
                {
                    var headers = SplitTableCells(lines[i]);
                    i += 2;
                    while (i < lines.Length && TableRowLine.IsMatch(lines[i]))
                    {
                        var cells = SplitTableCells(lines[i]);
                        if (cells.Count > 0)
                        {
                            sb.Append("• ").Append(cells[0]);
                            for (var c = 1; c < cells.Count; c++)
                            {
                                var label = c < headers.Count ? headers[c] : $"col{c + 1}";
                                if (string.IsNullOrWhiteSpace(label))
                                {
                                    label = $"col{c + 1}";
                                }

                                sb.Append('\n').Append("  - ").Append(label).Append(": ").Append(cells[c]);
                            }

                            sb.Append('\n');
                        }

                        i++;
                    }

                    sb.Append('\n');
                    continue;
                }

                sb.AppendLine(lines[i]);
                i++;
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        private static List<string> SplitTableCells(string line)
        {
            var t = (line ?? string.Empty).Trim();
            if (t.StartsWith('|'))
            {
                t = t[1..];
            }

            if (t.EndsWith('|'))
            {
                t = t[..^1];
            }

            return t.Split('|')
                .Select(c => c.Trim())
                .ToList();
        }

        private static string ApplyRtlToMarkdown(string md)
        {
            if (!IsMostlyRtl(md))
            {
                return md;
            }

            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(md.Length + lines.Length);
            var inCode = false;
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    sb.AppendLine(line);
                    continue;
                }

                if (inCode || string.IsNullOrWhiteSpace(line) || !IsMostlyRtl(line))
                {
                    sb.AppendLine(line);
                    continue;
                }

                var m = HeadingMd.Match(line);
                if (m.Success)
                {
                    sb.Append(m.Groups[1].Value).Append(' ')
                        .Append(Rlm)
                        .Append(m.Groups[2].Value.TrimStart(Rlm, ' '))
                        .AppendLine();
                    continue;
                }

                if (line.StartsWith(Rlm) || line.StartsWith(Rle))
                {
                    sb.AppendLine(line);
                }
                else
                {
                    sb.Append(Rlm).Append(line).AppendLine();
                }
            }

            var body = sb.ToString().TrimEnd();
            if (!body.StartsWith(Rle) && !body.StartsWith(Rlm))
            {
                body = Rle + body + Pdf;
            }

            return body;
        }

        private static string ApplyRtlToTelegramHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html) || !IsMostlyRtl(StripTags(html)))
            {
                return html;
            }

            var lines = html.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(html.Length + lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.Append('\n');
                    continue;
                }

                var plain = StripTags(line);
                if (IsMostlyRtl(plain)
                    && !line.Contains("<pre", StringComparison.OrdinalIgnoreCase)
                    && line.Length > 0
                    && line[0] != Rlm
                    && line[0] != Rle)
                {
                    sb.Append(Rlm);
                }

                sb.Append(line).Append('\n');
            }

            return sb.ToString().TrimEnd();
        }

        private static string StripControlMarks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace(Rlm.ToString(), string.Empty)
                .Replace(Rle.ToString(), string.Empty)
                .Replace(Pdf.ToString(), string.Empty)
                .Replace("\u200E", string.Empty);
        }

        private static string StripTags(string text)
            => Regex.Replace(text ?? string.Empty, @"<[^>]+>", string.Empty);

        private static string DecodeEntities(string text)
            => System.Net.WebUtility.HtmlDecode(text ?? string.Empty);

        private static bool IsRtlChar(char ch)
        {
            return (ch >= '\u0590' && ch <= '\u05FF')
                   || (ch >= '\u0600' && ch <= '\u06FF')
                   || (ch >= '\u0750' && ch <= '\u077F')
                   || (ch >= '\u08A0' && ch <= '\u08FF')
                   || (ch >= '\uFB50' && ch <= '\uFDFF')
                   || (ch >= '\uFE70' && ch <= '\uFEFF');
        }
    }
}
