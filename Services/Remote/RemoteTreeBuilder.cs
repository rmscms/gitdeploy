using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using GitDeployPro.Services;

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

        private static readonly NodeVisual DirectoryVisual = new(
            "📁", "#FFE7B95B", "DIR", "#1F4A391A", "#3A7C5E2A", "#FFE7B95B");
        private static readonly NodeVisual PlaceholderVisual = new(
            "⏳", "#FF8B96A6", "WAIT", "#1F2C3440", "#3A3D4C5F", "#FF8B96A6");
        private static readonly NodeVisual DefaultFileVisual = new(
            "📄", "#FFAAB5C4", "FILE", "#1F2E3948", "#3A3D5268", "#FFAAB5C4");
        private static readonly NodeVisual ConfigVisual = new(
            "🧩", "#FF74B6F5", "CFG", "#1F1E3752", "#3A2A4D73", "#FF74B6F5");
        private static readonly NodeVisual PhpVisual = new(
            "🐘", "#FFB0A3FF", "PHP", "#1F3E3658", "#3A5A4B87", "#FFB0A3FF");
        private static readonly NodeVisual JsVisual = new(
            "🟨", "#FFF7DF5E", "JS", "#1F4A4212", "#3A7B6A1D", "#FFF7DF5E");
        private static readonly NodeVisual TsVisual = new(
            "🔷", "#FF74A9FF", "TS", "#1F1A3252", "#3A264A75", "#FF74A9FF");
        private static readonly NodeVisual CssVisual = new(
            "🎨", "#FF64D2FF", "CSS", "#1F193C50", "#3A2A5C75", "#FF64D2FF");
        private static readonly NodeVisual HtmlVisual = new(
            "🌐", "#FFFFAE72", "HTML", "#1F4A2E18", "#3A6F4528", "#FFFFAE72");
        private static readonly NodeVisual JsonVisual = new(
            "🧾", "#FF7EE2A8", "JSON", "#1F1E4030", "#3A2D654A", "#FF7EE2A8");
        private static readonly NodeVisual SqlVisual = new(
            "🗄", "#FFFF9A86", "SQL", "#1F4C2822", "#3A7B3D35", "#FFFF9A86");
        private static readonly NodeVisual ImageVisual = new(
            "🖼", "#FFFFA5D6", "IMG", "#1F4A203A", "#3A75305C", "#FFFFA5D6");
        private static readonly NodeVisual ArchiveVisual = new(
            "🗜", "#FFC8A879", "ZIP", "#1F3D2B1F", "#3A5D4330", "#FFC8A879");
        private static readonly NodeVisual MarkdownVisual = new(
            "📝", "#FF8FC4FF", "MD", "#1F1F3755", "#3A305079", "#FF8FC4FF");
        private static readonly NodeVisual TextVisual = new(
            "📄", "#FFB7C0CD", "TXT", "#1F2A3342", "#3A3E4D62", "#FFB7C0CD");
        private static readonly NodeVisual ScriptVisual = new(
            "⚙", "#FF8EE39A", "SH", "#1F1A3D29", "#3A28613F", "#FF8EE39A");

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
            else
            {
                // Prefer last segment for mapped roots like /gitdeploy → show "/gitdeploy"
                // Keep leading slash so it reads as the browse root.
                if (!displayName.StartsWith('/'))
                {
                    displayName = "/" + displayName;
                }
            }

            var visual = DirectoryVisual;
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

        private static NodeVisual ResolveVisual(string? fileName, bool isDirectory, bool isPlaceholder)
        {
            if (isPlaceholder)
            {
                return PlaceholderVisual;
            }

            if (isDirectory)
            {
                return DirectoryVisual;
            }

            var name = fileName ?? string.Empty;
            var lower = name.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lower))
            {
                return DefaultFileVisual;
            }

            if (lower is ".env" or ".env.example" or ".htaccess" or ".editorconfig" or ".gitignore" or ".gitattributes")
            {
                return ConfigVisual;
            }

            if (lower.EndsWith(".blade.php", StringComparison.Ordinal))
            {
                return PhpVisual;
            }

            var ext = Path.GetExtension(lower);
            return ext switch
            {
                ".php" or ".phtml" => PhpVisual,
                ".js" or ".mjs" or ".cjs" or ".vue" => JsVisual,
                ".ts" or ".tsx" => TsVisual,
                ".css" or ".scss" or ".sass" or ".less" => CssVisual,
                ".html" or ".htm" or ".xhtml" or ".xaml" or ".xml" => HtmlVisual,
                ".json" or ".yaml" or ".yml" or ".toml" or ".ini" or ".conf" => JsonVisual,
                ".sql" => SqlVisual,
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" or ".bmp" => ImageVisual,
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => ArchiveVisual,
                ".md" or ".markdown" => MarkdownVisual,
                ".txt" or ".log" => TextVisual,
                ".ps1" or ".sh" or ".bat" or ".cmd" => ScriptVisual,
                _ => DefaultFileVisual
            };
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
