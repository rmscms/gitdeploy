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
        public string HealthDetails { get; set; } = string.Empty;
        public bool HealthPassed { get; set; }
    }
}

