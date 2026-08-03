using System.Collections.Generic;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Theme
{
    public sealed class ThemePack
    {
        [JsonProperty("schema")]
        public string Schema { get; set; } = ThemeTokenCatalog.SchemaId;

        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty("basedOn")]
        public string? BasedOn { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; } = "1";

        [JsonProperty("author")]
        public string? Author { get; set; }

        [JsonProperty("palette")]
        public Dictionary<string, string> Palette { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("deploy")]
        public ThemePackDeploySection Deploy { get; set; } = new();
    }

    public sealed class ThemePackDeploySection
    {
        [JsonProperty("shell")]
        public Dictionary<string, string> Shell { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("header")]
        public Dictionary<string, string> Header { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("branches")]
        public Dictionary<string, string> Branches { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("changedFiles")]
        public Dictionary<string, string> ChangedFiles { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("directUpload")]
        public Dictionary<string, string> DirectUpload { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("ftp")]
        public ThemePackFtpSection Ftp { get; set; } = new();

        [JsonProperty("logs")]
        public Dictionary<string, string> Logs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("terminal")]
        public ThemePackTerminalSection Terminal { get; set; } = new();

        [JsonProperty("editor")]
        public Dictionary<string, string> Editor { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("diff")]
        public Dictionary<string, string> Diff { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ThemePackFtpSection
    {
        [JsonProperty("chrome")]
        public Dictionary<string, string> Chrome { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("fileTypes")]
        public Dictionary<string, ThemePackFileTypeVisual> FileTypes { get; set; } =
            new(System.StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ThemePackFileTypeVisual
    {
        [JsonProperty("icon")]
        public string? Icon { get; set; }

        [JsonProperty("badgeBg")]
        public string? BadgeBg { get; set; }

        [JsonProperty("badgeBorder")]
        public string? BadgeBorder { get; set; }

        [JsonProperty("badgeFg")]
        public string? BadgeFg { get; set; }
    }

    public sealed class ThemePackTerminalSection
    {
        [JsonProperty("hostBackground")]
        public string? HostBackground { get; set; }

        [JsonProperty("statusConnected")]
        public string? StatusConnected { get; set; }

        [JsonProperty("statusDisconnected")]
        public string? StatusDisconnected { get; set; }

        [JsonProperty("statusConnecting")]
        public string? StatusConnecting { get; set; }

        [JsonProperty("statusError")]
        public string? StatusError { get; set; }

        [JsonProperty("xterm")]
        public Dictionary<string, string> Xterm { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        [JsonProperty("textPresets")]
        public List<string> TextPresets { get; set; } = new();
    }
}
