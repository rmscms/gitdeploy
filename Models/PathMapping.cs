namespace GitDeployPro.Models
{
    public class PathMapping
    {
        public string LocalPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{LocalPath} → {RemotePath}";
        }
    }
}

