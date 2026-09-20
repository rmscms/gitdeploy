using System;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Splits mashed agent replies into "understanding" vs "result" with a clear separator for Telegram.
    /// Mid-turn narration and the final outcome often arrive in one agent_message stream.
    /// </summary>
    public static class TelegramAgentReplyFormatter
    {
        public const string Separator = "────────";

        private static readonly Regex ExistingSeparator = new(
            @"^\s*[─\-—_]{3,}\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ResultAnchor = new(
            @"(?:^|\n|\.\s*|۔\s*)(?:" +
            @"✅\s*(?:انجام\s*شد|Done|Fixed|Complete[d]?|Ready)|" +
            @"(?:^|\n)\s*📌\s|" +
            @"(?:^|\n)\s*<b>|" +
            @"(?:^|\n)\s*##\s|" +
            @"(?:نتیجه|Result)\s*[:：]" +
            @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Ensures a visual break between investigation narration and the work result when both are present.
        /// </summary>
        public static string Format(string? output)
        {
            var text = (output ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (ExistingSeparator.IsMatch(text))
            {
                return ExistingSeparator.Replace(text, Separator).Trim();
            }

            if (TrySplitAtResultAnchor(text, out var head, out var tail))
            {
                return JoinParts(head, tail);
            }

            if (TrySplitAtParagraphBreak(text, out head, out tail))
            {
                return JoinParts(head, tail);
            }

            return text;
        }

        /// <summary>
        /// Joins earlier assistant segments (narration) with the last segment (result).
        /// </summary>
        public static string JoinSegments(string? narration, string? result)
        {
            var head = (narration ?? string.Empty).Trim();
            var tail = (result ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(head))
            {
                return Format(tail);
            }

            if (string.IsNullOrEmpty(tail))
            {
                return Format(head);
            }

            return JoinParts(head, Format(tail));
        }

        private static string JoinParts(string head, string tail)
        {
            head = head.Trim();
            tail = tail.Trim();
            if (string.IsNullOrEmpty(head))
            {
                return tail;
            }

            if (string.IsNullOrEmpty(tail))
            {
                return head;
            }

            return head + "\n\n" + Separator + "\n\n" + tail;
        }

        private static bool TrySplitAtResultAnchor(string text, out string head, out string tail)
        {
            head = string.Empty;
            tail = string.Empty;

            var match = ResultAnchor.Match(text);
            if (!match.Success)
            {
                return false;
            }

            // Prefer the last strong result marker so earlier ✅ in narration does not win.
            Match? best = null;
            for (var m = match; m.Success; m = m.NextMatch())
            {
                best = m;
            }

            if (best == null)
            {
                return false;
            }

            var idx = best.Index;
            // If the match consumed a leading ". " / newline, keep that in head.
            var value = best.Value;
            var trimLeading = 0;
            if (value.StartsWith("\n", StringComparison.Ordinal))
            {
                trimLeading = 1;
            }
            else if (value.StartsWith(". ", StringComparison.Ordinal) || value.StartsWith("۔ ", StringComparison.Ordinal))
            {
                trimLeading = 2;
                idx += 1; // keep the period on the head sentence
            }

            var splitAt = idx + trimLeading;
            if (splitAt < 24 || splitAt >= text.Length - 8)
            {
                return false;
            }

            head = text[..splitAt].TrimEnd();
            tail = text[splitAt..].TrimStart();
            return head.Length >= 20 && tail.Length >= 8;
        }

        private static bool TrySplitAtParagraphBreak(string text, out string head, out string tail)
        {
            head = string.Empty;
            tail = string.Empty;

            var gap = text.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (gap < 24 || gap >= text.Length - 12)
            {
                return false;
            }

            head = text[..gap].Trim();
            tail = text[(gap + 2)..].Trim();
            if (head.Length < 20 || tail.Length < 8)
            {
                return false;
            }

            // Only split when the first block looks like mid-work narration.
            if (!LooksLikeNarration(head) || LooksLikeNarration(tail))
            {
                return false;
            }

            return true;
        }

        private static bool LooksLikeNarration(string block)
        {
            var sample = block.Length > 220 ? block[..220] : block;
            return sample.Contains("در حال", StringComparison.Ordinal)
                   || sample.Contains("دارم ", StringComparison.Ordinal)
                   || sample.Contains("الان ", StringComparison.Ordinal)
                   || sample.Contains("چک می‌کنم", StringComparison.Ordinal)
                   || sample.Contains("Looking at", StringComparison.OrdinalIgnoreCase)
                   || sample.Contains("I'm ", StringComparison.OrdinalIgnoreCase)
                   || sample.Contains("I am ", StringComparison.OrdinalIgnoreCase)
                   || sample.Contains("Investigat", StringComparison.OrdinalIgnoreCase)
                   || sample.Contains("Opening ", StringComparison.OrdinalIgnoreCase)
                   || sample.Contains("Checking ", StringComparison.OrdinalIgnoreCase);
        }
    }
}
