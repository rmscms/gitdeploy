using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GitDeployPro.Services
{
    public static class DiffParser
    {
        public static Dictionary<string, string> SplitByFile(string diffOutput)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(diffOutput))
            {
                return map;
            }

            using var reader = new StringReader(diffOutput);
            string? line;
            string? currentFile = null;
            var builder = new StringBuilder();

            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("diff --git", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(currentFile) && builder.Length > 0)
                    {
                        map[currentFile] = builder.ToString().TrimEnd();
                    }

                    builder.Clear();
                    currentFile = ExtractFileName(line);
                    continue;
                }

                if (currentFile != null)
                {
                    builder.AppendLine(line);
                }
            }

            if (!string.IsNullOrEmpty(currentFile) && builder.Length > 0)
            {
                map[currentFile] = builder.ToString().TrimEnd();
            }

            return map;
        }

        private static string ExtractFileName(string diffHeader)
        {
            // diff --git a/path b/path
            var parts = diffHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var path = parts[3];
                if (path.StartsWith("b/"))
                {
                    path = path.Substring(2);
                }
                return path;
            }

            return parts.Length > 2 ? parts[^1] : diffHeader;
        }

        public static string BuildAddedFileDiff(string relativePath, string content)
        {
            var normalized = NormalizePath(relativePath);
            var builder = new StringBuilder();
            builder.AppendLine("--- /dev/null");
            builder.AppendLine($"+++ b/{normalized}");

            var fileLines = content.Replace("\r\n", "\n").Split('\n');
            builder.AppendLine($"@@ -0,0 +1,{Math.Max(1, fileLines.Length)} @@");
            foreach (var line in fileLines)
            {
                builder.AppendLine("+" + line);
            }
            return builder.ToString().TrimEnd();
        }

        private static string NormalizePath(string relativePath)
        {
            return relativePath.Replace("\\", "/").Trim();
        }
    }
}


