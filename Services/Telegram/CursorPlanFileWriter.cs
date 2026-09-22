using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>Writes plan markdown under {project}/.cursor/plans/ (gitignored).</summary>
    internal static class CursorPlanFileWriter
    {
        public static string Write(string projectPath, string name, string overview, string planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException(projectPath);
            }

            var plansDir = EnsurePlansDirectory(projectPath);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var slug = SanitizeAsciiSlug(name);
            var fileName = string.IsNullOrWhiteSpace(slug)
                ? $"plan-{stamp}.md"
                : $"{slug}-{stamp}.md";
            var path = Path.Combine(plansDir, fileName);

            var title = string.IsNullOrWhiteSpace(name) ? "Plan" : name.Trim();
            title = TelegramTextFormat.HtmlToMarkdown(title);
            overview = TelegramTextFormat.HtmlToMarkdown(overview);
            planMarkdown = TelegramTextFormat.HtmlToMarkdown(planMarkdown);

            var sb = new StringBuilder();
            sb.AppendLine("# " + title.TrimStart('#', ' ', '\u200F', '\u202B', '\u202C'));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(overview))
            {
                sb.AppendLine(overview.Trim());
                sb.AppendLine();
            }

            sb.AppendLine((planMarkdown ?? string.Empty).Trim());
            sb.AppendLine();

            // One pass: tables→lists + RTL marks — Telegram-compatible Markdown.
            var body = TelegramTextFormat.ToTelegramCompatibleMarkdown(sb.ToString());
            File.WriteAllText(path, body + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // Bump mtime explicitly so "latest by date" is unambiguous.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            CursorPlanCatalog.Remember(projectPath, path);
            return path;
        }

        /// <summary>
        /// When ACP create_plan did not fire, persist the plan-mode reply body as a dated .md.
        /// </summary>
        public static string? WriteFromReply(string projectPath, string? replyBody)
        {
            var body = StripReplyNoise(replyBody);
            if (body.Length < 80)
            {
                return null;
            }

            var title = GuessTitle(body);
            return Write(projectPath, title, overview: string.Empty, planMarkdown: body);
        }

        public static string EnsurePlansDirectory(string projectPath)
        {
            var plansDir = Path.Combine(projectPath, ".cursor", "plans");
            Directory.CreateDirectory(plansDir);

            var localIgnore = Path.Combine(plansDir, ".gitignore");
            if (!File.Exists(localIgnore))
            {
                File.WriteAllText(localIgnore, "*\n!.gitignore\n", new UTF8Encoding(false));
            }

            EnsureRootGitignoreEntry(projectPath);
            return plansDir;
        }

        private static void EnsureRootGitignoreEntry(string projectPath)
        {
            try
            {
                var gi = Path.Combine(projectPath, ".gitignore");
                const string entry = ".cursor/plans/";
                if (!File.Exists(gi))
                {
                    File.WriteAllText(gi, entry + "\n", new UTF8Encoding(false));
                    return;
                }

                var text = File.ReadAllText(gi);
                if (text.Contains(entry, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(".cursor/plans", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var suffix = text.EndsWith("\n", StringComparison.Ordinal) ? entry + "\n" : "\n" + entry + "\n";
                File.AppendAllText(gi, suffix, new UTF8Encoding(false));
            }
            catch
            {
                // Best-effort — plan write must not fail on gitignore.
            }
        }

        /// <summary>Telegram multipart filename: always ends with .md, ASCII-safe.</summary>
        public static string TelegramUploadFileName(string diskPath)
        {
            var raw = Path.GetFileName(diskPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return $"plan-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md";
            }

            if (Regex.IsMatch(raw, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}\.md$", RegexOptions.CultureInvariant))
            {
                return raw;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var slug = SanitizeAsciiSlug(Path.GetFileNameWithoutExtension(raw));
            return string.IsNullOrWhiteSpace(slug)
                ? $"plan-{stamp}.md"
                : $"{slug}-{stamp}.md";
        }

        public static string StripReplyNoise(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var t = text.Replace("\r\n", "\n").Trim();
            t = TelegramDocumentDispatch.StripMarkers(t);
            t = Regex.Replace(
                t,
                @"\n+(?:Usage:.*|⚡.*|🧠.*|n/a \(ACP\))\s*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // Drop Telegram separator narration head if present — keep full plan body.
            return t.Trim();
        }

        private static string GuessTitle(string body)
        {
            var first = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "plan";
            first = Regex.Replace(first, @"^#+\s*", string.Empty).Trim();
            first = Regex.Replace(first, @"<[^>]+>", string.Empty).Trim();
            if (first.Length > 48)
            {
                first = first[..48].Trim();
            }

            return string.IsNullOrWhiteSpace(first) ? "plan" : first;
        }

        private static string SanitizeAsciiSlug(string name)
        {
            var raw = (name ?? string.Empty).Trim();
            raw = Regex.Replace(raw, @"\.(plan\.)?md$", string.Empty, RegexOptions.IgnoreCase);
            raw = Regex.Replace(raw, @"[^a-zA-Z0-9]+", "-");
            raw = Regex.Replace(raw, @"-+", "-").Trim('-');
            if (raw.Length > 40)
            {
                raw = raw[..40].Trim('-');
            }

            return raw;
        }
    }
}
