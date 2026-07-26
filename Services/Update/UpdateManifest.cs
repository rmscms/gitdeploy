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

        [JsonProperty("mandatory")]
        public bool Mandatory { get; set; }

        [JsonProperty("publishedUtc")]
        public string? PublishedUtc { get; set; }
    }
}
