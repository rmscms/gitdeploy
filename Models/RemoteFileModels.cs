using System;

namespace GitDeployPro.Models
{
    public sealed class RemoteDirectoryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? ModifiedUtc { get; set; }
    }

    public sealed class RemoteFileStat
    {
        public string FullPath { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? ModifiedUtc { get; set; }
    }

    public sealed class RemoteUploadProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
        public bool IsIndeterminate { get; set; }
    }
}
