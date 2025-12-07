namespace GitDeployPro.Models
{
    public class BackupHealthReport
    {
        public bool IsHealthy { get; set; }
        public string Details { get; set; } = string.Empty;
        public string Algorithm { get; set; } = "SHA256+Structure";
    }
}

