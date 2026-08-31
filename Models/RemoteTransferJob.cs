namespace GitDeployPro.Models
{
    public sealed class RemoteTransferJob
    {
        public string RemotePath { get; init; } = string.Empty;
        public string LocalPath { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Tag { get; init; } = string.Empty;
        public object? Source { get; init; }
        public string DisplayName
        {
            get
            {
                var remote = System.IO.Path.GetFileName((RemotePath ?? string.Empty).Replace('\\', '/').TrimEnd('/'));
                if (!string.IsNullOrWhiteSpace(remote))
                {
                    return remote;
                }

                var local = System.IO.Path.GetFileName(LocalPath);
                if (!string.IsNullOrWhiteSpace(local))
                {
                    return local;
                }

                var tag = System.IO.Path.GetFileName((Tag ?? string.Empty).Replace('\\', '/'));
                return string.IsNullOrWhiteSpace(tag) ? "file" : tag;
            }
        }
    }
}
