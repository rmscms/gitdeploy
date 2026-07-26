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
    }
}
