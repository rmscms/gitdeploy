using System;
using System.Collections.Generic;

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

    public sealed class ParallelWorkerSnapshot
    {
        public int Id { get; init; }
        public string IdLabel => $"W{Id}";
        public string State { get; init; } = "Idle";
        public string FileName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public int FilesDone { get; init; }
    }

    public sealed class ParallelTransferProgress
    {
        public string Phase { get; init; } = string.Empty;
        public int RequestedWorkers { get; init; }
        public int ActiveWorkers { get; init; }
        public int Completed { get; init; }
        public int Total { get; init; }
        public int Skipped { get; init; }
        public int ErrorCount { get; init; }
        public long BytesTransferred { get; init; }
        public long TotalBytes { get; init; }
        public double Percent { get; init; }
        public bool IsIndeterminate { get; init; }
        public string Headline { get; init; } = string.Empty;
        public string LastLine { get; init; } = string.Empty;
        public long Sequence { get; init; }
        public IReadOnlyList<ParallelWorkerSnapshot> Workers { get; init; } = Array.Empty<ParallelWorkerSnapshot>();
    }

    public sealed class RemoteUploadProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
        public bool IsIndeterminate { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public ParallelTransferProgress? Detail { get; set; }
    }

    public sealed class RemoteDownloadProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
        public bool IsIndeterminate { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public ParallelTransferProgress? Detail { get; set; }
    }
}
