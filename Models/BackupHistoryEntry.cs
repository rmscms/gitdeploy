using System;
using System.Collections.Generic;
using GitDeployPro.Services;
using Newtonsoft.Json;

namespace GitDeployPro.Models
{
    public class BackupHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ScheduleId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public string ConnectionProfileId { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; } = AppTimeService.UtcNow;
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
        public DateTime? IntegritySampleCapturedUtc { get; set; }
        public string IntegritySampleMessage { get; set; } = string.Empty;
        public List<BackupIntegrityTableSample> IntegrityTableSamples { get; set; } = new();

        [JsonIgnore]
        public DateTime StartedLocal => AppTimeService.ToLocalFromUtc(StartedUtc);

        [JsonIgnore]
        public DateTime? CompletedLocal => AppTimeService.ToLocalFromUtc(CompletedUtc);

        [JsonIgnore]
        public string StartedLocalDisplay => AppTimeService.FormatLocalFromUtc(StartedUtc);

        [JsonIgnore]
        public string CompletedLocalDisplay => AppTimeService.FormatLocalFromUtc(CompletedUtc);

        [JsonIgnore]
        public string IntegritySampleCapturedLocalDisplay => AppTimeService.FormatLocalFromUtc(IntegritySampleCapturedUtc);

        [JsonIgnore]
        public bool HasIntegritySamples => IntegrityTableSamples != null && IntegrityTableSamples.Count > 0;
    }
}

