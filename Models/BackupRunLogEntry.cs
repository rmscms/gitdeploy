using System;
using GitDeployPro.Services;

namespace GitDeployPro.Models
{
    public class BackupRunLogEntry
    {
        public DateTime Timestamp { get; set; } = AppTimeService.LocalNow;
        public string Message { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }
}

