namespace GitDeployPro.Models
{
    public class ImportProgressUpdate
    {
        public long BytesProcessed { get; set; }
        public long TotalBytes { get; set; }
        public int StatementsExecuted { get; set; }
        public string? Message { get; set; }
    }
}

