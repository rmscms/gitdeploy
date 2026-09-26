using System;
using System.Text.RegularExpressions;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Single-line normalize plus a denylist of dangerous categories.
    /// Build tools such as go build / php artisan stay allowed.
    /// </summary>
    internal static class TelegramSshCommandGuard
    {
        public const int MaxLength = 500;

        public readonly record struct Result(bool Ok, string Command, string? BlockKey);

        public static Result Evaluate(string? raw)
        {
            var text = StripMarks(raw ?? string.Empty).Trim();
            if (text.Contains('\n') || text.Contains('\r'))
            {
                return new Result(false, string.Empty, "telegram.termBlockMultiline");
            }

            text = Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
            if (text.Length == 0)
            {
                return new Result(false, string.Empty, "telegram.termBlockEmpty");
            }

            if (text.Length > MaxLength)
            {
                return new Result(false, string.Empty, "telegram.termBlockTooLong");
            }

            var category = MatchDanger(text);
            if (category != null)
            {
                return new Result(false, string.Empty, category);
            }

            if (IsInteractive(text))
            {
                return new Result(false, string.Empty, "telegram.termBlockInteractive");
            }

            if (HasChain(text))
            {
                return new Result(false, string.Empty, "telegram.termBlockChain");
            }

            return new Result(true, text, null);
        }

        public static string BlockMessage(string? blockKey)
        {
            var key = string.IsNullOrWhiteSpace(blockKey) ? "telegram.termBlockChain" : blockKey;
            return Loc.T("telegram.termBlocked", Loc.T(key));
        }

        private static string StripMarks(string text)
        {
            return text
                .Replace("\u200F", string.Empty)
                .Replace("\u200E", string.Empty)
                .Replace("\u202A", string.Empty)
                .Replace("\u202B", string.Empty)
                .Replace("\u202C", string.Empty)
                .Replace("\u202D", string.Empty)
                .Replace("\u202E", string.Empty)
                .Replace("\uFEFF", string.Empty);
        }

        private static bool HasChain(string text)
        {
            return text.Contains("&&", StringComparison.Ordinal)
                   || text.Contains("||", StringComparison.Ordinal)
                   || text.Contains(';')
                   || text.Contains('|')
                   || text.Contains('`')
                   || text.Contains("$(", StringComparison.Ordinal);
        }

        private static string? MatchDanger(string text)
        {
            if (IsWideDelete(text))
            {
                return "telegram.termBlockWipe";
            }

            if (Regex.IsMatch(text, @"\b(mkfs|fdisk|parted)\b", RegexOptions.IgnoreCase)
                || (Regex.IsMatch(text, @"\bdd\b", RegexOptions.IgnoreCase)
                    && text.Contains("/dev/", StringComparison.OrdinalIgnoreCase))
                || Regex.IsMatch(text, @">\s*/dev/(sd|nvme|disk)", RegexOptions.IgnoreCase))
            {
                return "telegram.termBlockDisk";
            }

            if (Regex.IsMatch(text, @"\b(shutdown|reboot|halt|poweroff)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"\binit\s+[06]\b", RegexOptions.IgnoreCase))
            {
                return "telegram.termBlockPower";
            }

            if (Regex.IsMatch(text, @":\(\)\s*\{", RegexOptions.CultureInvariant))
            {
                return "telegram.termBlockFork";
            }

            if (IsDangerousChmod(text) || IsDangerousChown(text))
            {
                return "telegram.termBlockPerm";
            }

            if (Regex.IsMatch(
                    text,
                    @"\b(curl|wget)\b.+\|\s*(sudo\s+)?(ba)?sh\b|\b(curl|wget)\b.+\|\s*(sudo\s+)?python",
                    RegexOptions.IgnoreCase))
            {
                return "telegram.termBlockFetch";
            }

            if (Regex.IsMatch(text, @"\b(userdel|passwd|visudo)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"\biptables\b.*(\s-F\b|--flush)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"\bufw\s+disable\b", RegexOptions.IgnoreCase))
            {
                return "telegram.termBlockAccount";
            }

            return null;
        }

        private static bool IsInteractive(string text)
        {
            if (Regex.IsMatch(
                    text,
                    @"(?:^|\s)(?:sudo\s+)?(nano|vim|nvim|vi|emacs|pico|joe|less|more|most|man|top|htop|btop|mc|ranger|screen|tmux|ssh|telnet|ftp|sftp)\b",
                    RegexOptions.IgnoreCase))
            {
                return true;
            }

            var dbClient = Regex.IsMatch(
                text,
                @"(?:^|\s)(?:sudo\s+)?(mysql|mariadb|psql|sqlite3|mongo|mongosh|redis-cli)\b",
                RegexOptions.IgnoreCase);
            var oneShot = Regex.IsMatch(text, @"(?:^|\s)(-e|--execute|-c|--command)\b", RegexOptions.IgnoreCase);
            if (dbClient && !oneShot)
            {
                return true;
            }

            var repl = Regex.IsMatch(
                text,
                @"(?:^|\s)(?:sudo\s+)?(python3?|node|irb|php)\b",
                RegexOptions.IgnoreCase);
            var hasScript = Regex.IsMatch(text, @"\.(py|js|php)\b", RegexOptions.IgnoreCase)
                            || Regex.IsMatch(text, @"(?:^|\s)php\s+artisan\b", RegexOptions.IgnoreCase)
                            || oneShot;
            return repl && !hasScript;
        }

        private static bool IsWideDelete(string text)
        {
            if (!Regex.IsMatch(text, @"\brm\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            return Regex.IsMatch(text, @"(^|\s)-[a-zA-Z]*r[a-zA-Z]*f\b", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(text, @"(^|\s)-[a-zA-Z]*f[a-zA-Z]*r\b", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(text, @"(^|\s)--recursive\b", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(text, @"(^|\s)-[Rr]\b", RegexOptions.IgnoreCase);
        }

        private static bool IsDangerousChmod(string text)
        {
            if (!Regex.IsMatch(text, @"\bchmod\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            var recursive = Regex.IsMatch(text, @"(^|\s)(-R|--recursive)\b", RegexOptions.IgnoreCase);
            var wide = Regex.IsMatch(text, @"\b777\b") || Regex.IsMatch(text, @"\ba\+rwx\b", RegexOptions.IgnoreCase);
            var root = Regex.IsMatch(text, @"(^|\s)/\s*$") || Regex.IsMatch(text, @"(^|\s)/\s");
            return recursive && wide && root;
        }

        private static bool IsDangerousChown(string text)
        {
            if (!Regex.IsMatch(text, @"\bchown\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            var recursive = Regex.IsMatch(text, @"(^|\s)(-R|--recursive)\b", RegexOptions.IgnoreCase);
            var root = Regex.IsMatch(text, @"(^|\s)/\s*$") || Regex.IsMatch(text, @"(^|\s)/\s");
            return recursive && root;
        }
    }
}
