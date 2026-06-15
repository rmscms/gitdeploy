using System;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public static class RemotePathResolver
    {
        public static PathMapping? GetPrimaryMapping(ConnectionProfile? profile)
        {
            if (profile?.PathMappings == null)
            {
                return null;
            }

            return profile.PathMappings.FirstOrDefault(pm =>
                pm != null &&
                (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)));
        }

        public static string BuildRemoteRoot(ConnectionProfile profile)
        {
            var mapping = GetPrimaryMapping(profile);
            var baseRemote = NormalizeRemoteBase(profile.RemotePath);
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.RemotePath))
            {
                return EnsureTrailingSlash(baseRemote);
            }

            return EnsureTrailingSlash(CombineRemotePaths(baseRemote, mapping.RemotePath));
        }

        public static string NormalizeRemoteBase(string? path)
        {
            var trimmed = (path ?? "/").Trim().Replace("\\", "/");
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed;
            }

            while (trimmed.Contains("//", StringComparison.Ordinal))
            {
                trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "/";
            }

            return trimmed;
        }

        public static string EnsureTrailingSlash(string path)
        {
            var normalized = NormalizeRemoteBase(path);
            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized;
        }

        public static string CombineRemotePaths(string baseRemote, string? childSegment)
        {
            var normalizedBase = NormalizeRemoteBase(baseRemote).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(childSegment))
            {
                return normalizedBase;
            }

            var segment = childSegment.Trim().Replace("\\", "/").Trim('/');
            if (string.IsNullOrWhiteSpace(segment))
            {
                return normalizedBase;
            }

            return $"{normalizedBase}/{segment}";
        }

        public static string GetParentDirectory(string path, string root)
        {
            var normalizedPath = NormalizeRemoteBase(path).TrimEnd('/');
            var normalizedRoot = EnsureTrailingSlash(root).TrimEnd('/');
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return EnsureTrailingSlash(normalizedRoot);
            }

            var index = normalizedPath.LastIndexOf('/', normalizedPath.Length - 1);
            if (index <= 0)
            {
                return EnsureTrailingSlash(normalizedRoot);
            }

            var parent = normalizedPath[..index];
            if (parent.Length < normalizedRoot.Length)
            {
                parent = normalizedRoot;
            }

            return EnsureTrailingSlash(parent);
        }
    }
}
