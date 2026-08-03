using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using GitDeployPro.Services.Theme;

namespace GitDeployPro.Services.Remote
{
    public sealed class RemoteTreeBuilder
    {
        private readonly record struct NodeVisual(
            string IconGlyph,
            string IconColor,
            string BadgeText,
            string BadgeBackground,
            string BadgeBorder,
            string BadgeForeground);

        public List<RemoteTreeNode> BuildNodes(IEnumerable<RemoteDirectoryEntry> entries)
        {
            var result = new List<RemoteTreeNode>();
            foreach (var entry in entries
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(CreateNode(entry));
            }

            return result;
        }

        public List<RemoteTreeNode> BuildFolderNodes(
            IEnumerable<RemoteDirectoryEntry> entries,
            string? blockedPathPrefix = null)
        {
            var result = new List<RemoteTreeNode>();
            foreach (var entry in entries
                .Where(e => e.IsDirectory)
                .Where(e => !IsBlockedPath(e.FullPath, blockedPathPrefix))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(CreateNode(entry));
            }

            return result;
        }

        public RemoteTreeNode CreateRootFolderNode(string remoteRoot)
        {
            var path = RemotePathResolver.EnsureTrailingSlash(remoteRoot);
            var displayName = path.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "/";
            }
            else if (!displayName.StartsWith('/'))
            {
                displayName = "/" + displayName;
            }

            var visual = ResolveVisual(displayName, isDirectory: true, isPlaceholder: false);
            var node = new RemoteTreeNode
            {
                Name = displayName,
                FullPath = string.IsNullOrWhiteSpace(path.TrimEnd('/')) ? "/" : path.TrimEnd('/'),
                IsDirectory = true,
                SizeLabel = "root",
                ModifiedLabel = "—",
                IconGlyph = visual.IconGlyph,
                IconColor = visual.IconColor,
                BadgeText = "ROOT",
                BadgeBackground = visual.BadgeBackground,
                BadgeBorder = visual.BadgeBorder,
                BadgeForeground = visual.BadgeForeground
            };
            node.Children.Add(CreateLoadingPlaceholder());
            return node;
        }

        private RemoteTreeNode CreateNode(RemoteDirectoryEntry entry)
        {
            var visual = ResolveVisual(entry.Name, entry.IsDirectory, isPlaceholder: false);
            var node = new RemoteTreeNode
            {
                Name = entry.Name,
                FullPath = entry.FullPath,
                IsDirectory = entry.IsDirectory,
                SizeBytes = entry.SizeBytes,
                SizeLabel = entry.IsDirectory ? "dir" : FormatSize(entry.SizeBytes),
                ModifiedLabel = entry.ModifiedUtc.HasValue
                    ? AppTimeService.FormatLocalFromUtc(entry.ModifiedUtc)
                    : "—",
                IconGlyph = visual.IconGlyph,
                IconColor = visual.IconColor,
                BadgeText = visual.BadgeText,
                BadgeBackground = visual.BadgeBackground,
                BadgeBorder = visual.BadgeBorder,
                BadgeForeground = visual.BadgeForeground
            };

            if (entry.IsDirectory)
            {
                node.Children.Add(CreateLoadingPlaceholder());
            }

            return node;
        }

        public static bool IsBlockedPath(string candidatePath, string? blockedPathPrefix)
        {
            if (string.IsNullOrWhiteSpace(blockedPathPrefix))
            {
                return false;
            }

            var candidate = RemotePathResolver.NormalizeRemoteBase(candidatePath).TrimEnd('/');
            var blocked = RemotePathResolver.NormalizeRemoteBase(blockedPathPrefix).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(blocked))
            {
                return false;
            }

            return string.Equals(candidate, blocked, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(blocked + "/", StringComparison.OrdinalIgnoreCase);
        }

        public RemoteTreeNode CreateLoadingPlaceholder()
        {
            var visual = ResolveVisual("Loading...", isDirectory: false, isPlaceholder: true);
            return new RemoteTreeNode
            {
                Name = "Loading...",
                FullPath = string.Empty,
                IsDirectory = false,
                IsPlaceholder = true,
                IconGlyph = visual.IconGlyph,
                IconColor = visual.IconColor,
                BadgeText = visual.BadgeText,
                BadgeBackground = visual.BadgeBackground,
                BadgeBorder = visual.BadgeBorder,
                BadgeForeground = visual.BadgeForeground
            };
        }

        /// <summary>Re-apply theme colors to an existing tree without rebuilding structure.</summary>
        public static void ApplyThemeToNodes(IEnumerable<RemoteTreeNode>? nodes)
        {
            if (nodes == null)
            {
                return;
            }

            foreach (var node in nodes)
            {
                var visual = ResolveVisual(node.Name, node.IsDirectory, node.IsPlaceholder);
                node.IconColor = visual.IconColor;
                if (!string.Equals(node.BadgeText, "ROOT", StringComparison.Ordinal))
                {
                    node.BadgeText = visual.BadgeText;
                }

                node.BadgeBackground = visual.BadgeBackground;
                node.BadgeBorder = visual.BadgeBorder;
                node.BadgeForeground = visual.BadgeForeground;
                if (node.Children is { Count: > 0 })
                {
                    ApplyThemeToNodes(node.Children);
                }
            }
        }

        private static NodeVisual ResolveVisual(string? fileName, bool isDirectory, bool isPlaceholder)
        {
            if (isPlaceholder)
            {
                return FromToken("placeholder", "⏳", "WAIT");
            }

            if (isDirectory)
            {
                return FromToken("directory", "📁", "DIR");
            }

            var name = fileName ?? string.Empty;
            var lower = name.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lower))
            {
                return FromToken("defaultFile", "📄", "FILE");
            }

            if (lower is ".env" or ".env.example" or ".htaccess" or ".editorconfig" or ".gitignore" or ".gitattributes")
            {
                return FromToken("config", "🧩", "CFG");
            }

            if (lower.EndsWith(".blade.php", StringComparison.Ordinal))
            {
                return FromToken("php", "🐘", "PHP");
            }

            var ext = Path.GetExtension(lower);
            return ext switch
            {
                ".php" or ".phtml" => FromToken("php", "🐘", "PHP"),
                ".js" or ".mjs" or ".cjs" or ".vue" => FromToken("js", "🟨", "JS"),
                ".ts" or ".tsx" => FromToken("ts", "🔷", "TS"),
                ".css" or ".scss" or ".sass" or ".less" => FromToken("css", "🎨", "CSS"),
                ".html" or ".htm" or ".xhtml" or ".xaml" or ".xml" => FromToken("html", "🌐", "HTML"),
                ".json" or ".yaml" or ".yml" or ".toml" or ".ini" or ".conf" => FromToken("json", "🧾", "JSON"),
                ".sql" => FromToken("sql", "🗄", "SQL"),
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" or ".bmp" => FromToken("img", "🖼", "IMG"),
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => FromToken("zip", "🗜", "ZIP"),
                ".md" or ".markdown" => FromToken("md", "📝", "MD"),
                ".txt" or ".log" => FromToken("txt", "📄", "TXT"),
                ".ps1" or ".sh" or ".bat" or ".cmd" => FromToken("sh", "⚙", "SH"),
                _ => FromToken("defaultFile", "📄", "FILE")
            };
        }

        private static NodeVisual FromToken(string key, string glyph, string badge)
        {
            var visual = ThemeService.Instance.CurrentTokens.GetFileType(key);
            var fallback = ThemeTokenCatalog.BuildDefaultFileTypes()[key];
            return new NodeVisual(
                glyph,
                visual.Icon ?? fallback.Icon ?? "#FFAAB5C4",
                badge,
                visual.BadgeBg ?? fallback.BadgeBg ?? "#1F2E3948",
                visual.BadgeBorder ?? fallback.BadgeBorder ?? "#3A3D5268",
                visual.BadgeFg ?? fallback.BadgeFg ?? "#FFAAB5C4");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }
    }
}
