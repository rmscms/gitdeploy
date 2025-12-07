namespace GitDeployPro.Models
{
    public class BackupProgressUpdate
    {
        public string Message { get; set; } = string.Empty;
        public int TotalTables { get; set; }
        public int ProcessedTables { get; set; }
        public string? Stage { get; set; }
        public string? CurrentTable { get; set; }
        public int CurrentTableIndex { get; set; }
        public long CurrentTableTotalRows { get; set; }
        public long CurrentTableProcessedRows { get; set; }
    }
}

