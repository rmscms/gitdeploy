using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>Writes Cursor ACP create_plan markdown under {project}/.cursor/plans/.</summary>
    internal static class CursorPlanFileWriter
    {
        public static string Write(string projectPath, string name, string overview, string planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException(projectPath);
            }

            var plansDir = Path.Combine(projectPath, ".cursor", "plans");
            Directory.CreateDirectory(plansDir);

            var safe = SanitizeFileName(name);
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = "plan";
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"{safe}-{stamp}.plan.md";
            var path = Path.Combine(plansDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("# " + (string.IsNullOrWhiteSpace(name) ? "Plan" : name.Trim()));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(overview))
            {
                sb.AppendLine(overview.Trim());
                sb.AppendLine();
            }

            sb.AppendLine(planMarkdown.Trim());
            sb.AppendLine();

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            var raw = (name ?? string.Empty).Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(c, '-');
            }

            raw = Regex.Replace(raw, @"\s+", "-");
            raw = Regex.Replace(raw, @"-+", "-").Trim('-');
            return raw.Length > 60 ? raw[..60] : raw;
        }
    }
}
