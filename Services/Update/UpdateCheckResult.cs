using System;

namespace GitDeployPro.Services.Update
{
    public sealed class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; init; }
        public bool IsMandatory { get; init; }
        public Version? CurrentVersion { get; init; }
        public Version? RemoteVersion { get; init; }
        public UpdateManifest? Manifest { get; init; }
        public string? Error { get; init; }
        public bool IsUpToDate => string.IsNullOrWhiteSpace(Error) && !IsUpdateAvailable && Manifest != null;

        public static UpdateCheckResult Failed(string error) => new()
        {
            Error = error
        };
    }
}
