using System;

namespace GitDeployPro.Models
{
    public class BackupHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ScheduleId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public string ConnectionProfileId { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public bool IsRemoteArtifact { get; set; }
        public bool HasLocalArtifact { get; set; } = true;
        public string RemoteArtifactPath { get; set; } = string.Empty;
        public long RemoteArtifactSizeBytes { get; set; }
        public string RemoteArtifactSha256 { get; set; } = string.Empty;
        public string DownloadPolicy { get; set; } = string.Empty;
        public bool RemoteArtifactDeletedAfterDownload { get; set; }
        public string RemoteCleanupMessage { get; set; } = string.Empty;
        public string HealthDetails { get; set; } = string.Empty;
        public bool HealthPassed { get; set; }
        public bool RestoreValidationEnabled { get; set; }
        public bool RestoreValidationAttempted { get; set; }
        public bool RestoreValidationPassed { get; set; }
        public string RestoreValidationMessage { get; set; } = string.Empty;
        public string RestoreValidationDatabase { get; set; } = string.Empty;
    }
}

