namespace GitDeployPro.Models
{
    public class PathMapping
    {
        public string LocalPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;

        /// <summary>
        /// True when <see cref="RemotePath"/> is a full server path outside the profile root
        /// (e.g. a sibling site on the same host) and must not be appended to the profile root.
        /// </summary>
        public bool RemoteIsAbsolute { get; set; }

        public override string ToString()
        {
            return $"{LocalPath} → {RemotePath}";
        }
    }
}

