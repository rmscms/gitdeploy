using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Resolves md/txt paths the user asked to send, plus optional [[TG_DOC:path]] markers in agent replies.
    /// </summary>
    internal static class TelegramDocumentDispatch
    {
        public const string MarkerPrefix = "[[TG_DOC:";
        public const string MarkerSuffix = "]]";

        private static readonly Regex MarkerRegex = new(
            @"\[\[TG_DOC:\s*(.+?)\s*\]\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PathRegex = new(
            @"(?i)(?<path>(?:[A-Za-z]:\\|\\\\|[\w.\-]+(?:[/\\]))[^\s""'<>|*?]+\.(?:md|markdown|mdc|txt))",
            RegexOptions.Compiled);

        private static readonly Regex SendIntent = new(
            @"(?i)(بفرست|ارسال\s*کن|ارسال\s*بشه|ارسال\s*شود|send\s+(?:it|the\s+)?(?:file|md|document)|attach)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string StripMarkers(string? reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return reply ?? string.Empty;
            }

            var cleaned = MarkerRegex.Replace(reply, string.Empty);
            return Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();
        }

        public static IReadOnlyList<string> CollectPaths(
            string projectPath,
            string? userText,
            string? agentReply)
        {
            var found = new List<string>();
            AddFromMarkers(agentReply, projectPath, found);

            var wantsSend = SendIntent.IsMatch(userText ?? string.Empty)
                            || SendIntent.IsMatch(agentReply ?? string.Empty)
                            || ContainsMarker(agentReply);
            if (wantsSend)
            {
                AddFromLoosePaths(userText, projectPath, found);
                AddFromLoosePaths(agentReply, projectPath, found);
            }

            return found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Take(5)
                .ToList();
        }

        private static bool ContainsMarker(string? text)
            => !string.IsNullOrEmpty(text) && text.IndexOf(MarkerPrefix, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void AddFromMarkers(string? text, string projectPath, List<string> sink)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in MarkerRegex.Matches(text))
            {
                AddResolved(m.Groups[1].Value, projectPath, sink);
            }
        }

        private static void AddFromLoosePaths(string? text, string projectPath, List<string> sink)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in PathRegex.Matches(text))
            {
                AddResolved(m.Groups["path"].Value.Trim().TrimEnd(')', ']', ',', ';', '.', '»', '«'), projectPath, sink);
            }
        }

        private static void AddResolved(string raw, string projectPath, List<string> sink)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var candidate = raw.Trim().Trim('`', '"', '\'');
            candidate = candidate.Replace('/', Path.DirectorySeparatorChar);

            string full;
            try
            {
                if (Path.IsPathRooted(candidate))
                {
                    full = Path.GetFullPath(candidate);
                }
                else if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
                {
                    full = Path.GetFullPath(Path.Combine(projectPath, candidate));
                }
                else
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            var ext = Path.GetExtension(full);
            if (ext is not (".md" or ".markdown" or ".mdc" or ".txt"))
            {
                return;
            }

            if (!sink.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                sink.Add(full);
            }
        }
    }
}
