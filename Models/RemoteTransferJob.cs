namespace GitDeployPro.Models
{
    public sealed class RemoteTransferJob
    {
        public string RemotePath { get; init; } = string.Empty;
        public string LocalPath { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Tag { get; init; } = string.Empty;
        public object? Source { get; init; }
        public string DisplayName =>
            string.IsNullOrWhiteSpace(RemotePath)
                ? System.IO.Path.GetFileName(LocalPath)
                : System.IO.Path.GetFileName(RemotePath.Replace('\\', '/'));
    }
}
