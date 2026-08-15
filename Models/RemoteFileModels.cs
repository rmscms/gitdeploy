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
        public DateTime? CreatedUtc { get; set; }
    }

    public sealed class RemoteFileStat
    {
        public string FullPath { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public DateTime? CreatedUtc { get; set; }
    }

    public sealed class RemoteUnixPermissionInfo
    {
        public bool Exists { get; init; } = true;
        public bool CanReadMode { get; init; }
        public bool CanChange { get; init; }
        public int Mode { get; init; }
        public string? Reason { get; init; }
    }

    public sealed class RemoteUploadProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
        public bool IsIndeterminate { get; set; }
    }

    public sealed class RemoteDownloadProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
        public bool IsIndeterminate { get; set; }
    }
}
