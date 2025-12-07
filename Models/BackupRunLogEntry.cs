using System;

namespace GitDeployPro.Models
{
    public class BackupRunLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Message { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }
}

