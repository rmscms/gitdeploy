using System;
using System.Collections.Generic;
using System.Linq;

namespace GitDeployPro.Services.Update
{
    public sealed class PendingUpdateState
    {
        public string Version { get; set; } = string.Empty;
        public string PackagePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool Mandatory { get; set; }
        public string ReleaseNotes { get; set; } = string.Empty;
        public List<string> Changelog { get; set; } = new();

        public IReadOnlyList<string> ResolveChangelogItems()
        {
            if (Changelog != null && Changelog.Count > 0)
            {
                return Changelog
                    .Select(x => (x ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            if (string.IsNullOrWhiteSpace(ReleaseNotes))
            {
                return Array.Empty<string>();
            }

            return ReleaseNotes
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('-', '*', '•', ' '))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }
    }

    public sealed class WhatsNewState
    {
        public string Version { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public List<string> Changelog { get; set; } = new();
        public string? PublishedUtc { get; set; }

        public IReadOnlyList<string> ResolveChangelogItems()
        {
            if (Changelog != null && Changelog.Count > 0)
            {
                return Changelog
                    .Select(x => (x ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            if (string.IsNullOrWhiteSpace(ReleaseNotes))
            {
                return Array.Empty<string>();
            }

            return ReleaseNotes
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('-', '*', '•', ' '))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }
    }
}
