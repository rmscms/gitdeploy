namespace GitDeployPro.Models
{
    public class DatabaseProcessInfo
    {
        public long Id { get; set; }
        public string User { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public int TimeSeconds { get; set; }
        public string State { get; set; } = string.Empty;
        public string Info { get; set; } = string.Empty;
    }
}


