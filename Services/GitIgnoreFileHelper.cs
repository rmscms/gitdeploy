using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services
{
    public static class GitIgnoreFileHelper
    {
        public static string GetFilePath(string projectPath)
            => Path.Combine(projectPath ?? string.Empty, ".gitignore");

        public static List<string> ReadLines(string projectPath)
        {
            var path = GetFilePath(projectPath);
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(path))
            {
                return new List<string>();
            }

            return File.ReadAllLines(path).ToList();
        }

        public static void WriteLines(string projectPath, IEnumerable<string> lines)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new InvalidOperationException("Project path is not valid.");
            }

            File.WriteAllLines(GetFilePath(projectPath), lines ?? Enumerable.Empty<string>());
        }

        public static string NormalizeRelative(string relativePath)
        {
            return (relativePath ?? string.Empty)
                .Replace('\\', '/')
                .Trim()
                .TrimStart('/');
        }

        public static string BuildEntry(string relativePath, bool isFolder)
        {
            var rel = NormalizeRelative(relativePath);
            if (string.IsNullOrWhiteSpace(rel))
            {
                return string.Empty;
            }

            var isRootItem = rel.IndexOf('/') < 0;
            if (isFolder)
            {
                rel = rel.TrimEnd('/') + "/";
            }

            return isRootItem ? "/" + rel : rel;
        }

        public static List<string> FindMatchingLines(IEnumerable<string> lines, string relativePath, bool isFolder)
        {
            var matches = new List<string>();
            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("!"))
                {
                    continue;
                }

                if (LineMatchesItem(trimmed, relativePath, isFolder))
                {
                    matches.Add(raw);
                }
            }

            return matches;
        }

        public static bool IsItemIgnored(IEnumerable<string> lines, string relativePath, bool isFolder)
            => FindMatchingLines(lines, relativePath, isFolder).Count > 0;

        public static bool TryAddEntry(string projectPath, string entry, out bool added)
        {
            added = false;
            var trimmed = (entry ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            var lines = ReadLines(projectPath);
            if (lines.Any(line => string.Equals(line.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            lines.Add(trimmed);
            WriteLines(projectPath, lines);
            added = true;
            return true;
        }

        public static bool TryRemoveMatching(string projectPath, string relativePath, bool isFolder, out List<string> removed)
        {
            removed = new List<string>();
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return false;
            }

            var lines = ReadLines(projectPath);
            var matches = FindMatchingLines(lines, relativePath, isFolder);
            if (matches.Count == 0)
            {
                return true;
            }

            var matchSet = new HashSet<string>(matches, StringComparer.Ordinal);
            var kept = lines.Where(line => !matchSet.Contains(line)).ToList();
            WriteLines(projectPath, kept);
            removed = matches
                .Select(line => line.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        public static bool TryRemoveExactLine(string projectPath, string line)
        {
            var target = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var lines = ReadLines(projectPath);
            var kept = lines
                .Where(existing => !string.Equals(existing.Trim(), target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count == lines.Count)
            {
                return false;
            }

            WriteLines(projectPath, kept);
            return true;
        }

        public static bool LineMatchesItem(string pattern, string relativePath, bool isFolder)
        {
            var rel = NormalizeRelative(relativePath);
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(rel))
            {
                return false;
            }

            var rooted = pattern.StartsWith('/') || pattern.StartsWith('\\');
            var glob = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
            var body = pattern.TrimStart('/', '\\').TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            var itemName = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
            if (isFolder)
            {
                itemName = itemName.TrimEnd('/', '\\');
            }

            if (glob)
            {
                var globPattern = pattern.TrimStart('/', '\\');
                if (globPattern.Contains('/') || globPattern.Contains('\\'))
                {
                    return MatchesWildcard(rel, globPattern.TrimEnd('/', '\\'));
                }

                if (rooted && rel.Contains('/'))
                {
                    return false;
                }

                return MatchesWildcard(itemName, globPattern.TrimEnd('/', '\\'));
            }

            if (string.Equals(rel, body, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rooted)
            {
                return false;
            }

            if (!body.Contains('/') && !body.Contains('\\'))
            {
                return string.Equals(itemName, body, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool MatchesWildcard(string value, string pattern)
        {
            try
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(value ?? string.Empty, regex, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
