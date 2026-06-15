using System;
using System.Collections.Generic;
using GitDeployPro.Services;
using Newtonsoft.Json;

namespace GitDeployPro.Models
{
    public sealed class BackupIntegritySamplingSnapshot
    {
        public DateTime CapturedUtc { get; set; } = AppTimeService.UtcNow;
        public string DatabaseName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<BackupIntegrityTableSample> Tables { get; set; } = new();

        [JsonIgnore]
        public bool HasData => Tables.Count > 0;
    }

    public sealed class BackupIntegrityTableSample
    {
        public int Rank { get; set; }
        public string TableName { get; set; } = string.Empty;
        public long ApproxRowCount { get; set; }
        public long DataBytes { get; set; }
        public long IndexBytes { get; set; }
        public long TotalBytes { get; set; }
        public string PrimaryKeySummary { get; set; } = string.Empty;
        public string LastRowStatus { get; set; } = string.Empty;
        public List<BackupIntegrityCellValue> LastRowValues { get; set; } = new();

        [JsonIgnore]
        public string TotalBytesLabel => FormatBytes(TotalBytes);

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var order = Math.Min(units.Length - 1, (int)Math.Floor(Math.Log(bytes, 1024)));
            var adjusted = bytes / Math.Pow(1024, order);
            return $"{adjusted:0.##} {units[order]}";
        }
    }

    public sealed class BackupIntegrityCellValue
    {
        public string ColumnName { get; set; } = string.Empty;
        public string DisplayValue { get; set; } = string.Empty;
    }
}
