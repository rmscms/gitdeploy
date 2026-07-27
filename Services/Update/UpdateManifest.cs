using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Update
{
    public sealed class UpdateManifest
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("fileName")]
        public string FileName { get; set; } = "";

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; } = "";

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = "";

        [JsonProperty("releaseNotes")]
        public string ReleaseNotes { get; set; } = "";

        /// <summary>Bullet list for the current version only (single source of truth).</summary>
        [JsonProperty("changelog")]
        public List<string>? Changelog { get; set; }

        [JsonProperty("mandatory")]
        public bool Mandatory { get; set; }

        [JsonProperty("publishedUtc")]
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
