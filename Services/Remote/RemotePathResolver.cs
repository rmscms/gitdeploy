using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public static class RemotePathResolver
    {
        public static PathMapping? GetPrimaryMapping(ConnectionProfile? profile)
        {
            return GetActiveMappings(profile).FirstOrDefault();
        }

        /// <summary>
        /// All configured mappings (non-null rows with at least one path set).
        /// </summary>
        public static IReadOnlyList<PathMapping> GetActiveMappings(ConnectionProfile? profile)
        {
            if (profile?.PathMappings == null)
            {
                return Array.Empty<PathMapping>();
            }

            return profile.PathMappings
                .Where(pm => pm != null &&
                             (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)))
                .Cast<PathMapping>()
                .ToList();
        }

        /// <summary>
        /// Empty, <c>/</c>, or <c>.</c> means the project root (not a subfolder).
        /// </summary>
        public static bool IsProjectRootLocalPath(string? localPath)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                return true;
            }

            var trimmed = localPath.Trim().Replace("\\", "/");
            return trimmed is "/" or "." or "";
        }

        /// <summary>
        /// Stores <c>/</c> for the project root; otherwise a relative segment like <c>site</c>.
        /// </summary>
        public static string NormalizeLocalMappingPath(string? input)
        {
            if (IsProjectRootLocalPath(input))
            {
                return "/";
            }

            var trimmed = input!.Trim().Replace("\\", "/").Trim('/');
            return string.IsNullOrWhiteSpace(trimmed) ? "/" : trimmed;
        }

        public static string FormatLocalMappingLabel(string? localPath)
        {
            return IsProjectRootLocalPath(localPath) ? "/" : NormalizeLocalMappingPath(localPath);
        }

        /// <summary>
        /// True when <paramref name="relativeProjectPath"/> is the mapped local folder itself
        /// or a file/folder under it (e.g. mapping <c>api</c> matches <c>api</c> and <c>api/app/x.php</c>,
        /// but not <c>core/...</c>). Empty mapping segment means whole project (always true).
        /// </summary>
        public static bool IsUnderLocalMapping(string? relativeProjectPath, string? mappingLocalSegment)
        {
            if (string.IsNullOrWhiteSpace(mappingLocalSegment) || IsProjectRootLocalPath(mappingLocalSegment))
            {
                return true;
            }

            var relative = (relativeProjectPath ?? string.Empty).Replace("\\", "/").Trim('/');
            var segment = NormalizeLocalMappingPath(mappingLocalSegment).Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                return true;
            }

            if (string.IsNullOrEmpty(relative))
            {
                return false;
            }

            return relative.Equals(segment, StringComparison.OrdinalIgnoreCase)
                   || relative.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolve which mapping owns a project-relative path. Longer local segments win
        /// (e.g. <c>api/public</c> before <c>api</c>). If any non-root mappings exist and none match, returns null (skip).
        /// If only a project-root mapping exists (or no mappings), uses profile remote base.
        /// </summary>
        public static bool TryResolveDeployTarget(
            string? relativeProjectPath,
            IReadOnlyList<PathMapping>? mappings,
            string profileRemoteBase,
            out string remoteFullPath,
            out PathMapping? matchedMapping,
            out string relativeUnderMapping)
        {
            remoteFullPath = string.Empty;
            matchedMapping = null;
            relativeUnderMapping = (relativeProjectPath ?? string.Empty).Replace("\\", "/").Trim('/');

            var profileBase = NormalizeRemoteBase(profileRemoteBase);
            var active = (mappings ?? Array.Empty<PathMapping>())
                .Where(pm => pm != null &&
                             (!string.IsNullOrWhiteSpace(pm.LocalPath) || !string.IsNullOrWhiteSpace(pm.RemotePath)))
                .ToList();

            if (active.Count == 0)
            {
                remoteFullPath = string.IsNullOrEmpty(relativeUnderMapping)
                    ? profileBase.TrimEnd('/')
                    : CombineRemotePaths(profileBase, relativeUnderMapping);
                return true;
            }

            // Prefer most specific (longest) local folder match.
            var ranked = active
                .Select(pm => new
                {
                    Mapping = pm,
                    Local = IsProjectRootLocalPath(pm.LocalPath)
                        ? string.Empty
                        : NormalizeLocalMappingPath(pm.LocalPath).Trim('/'),
                    IsRoot = IsProjectRootLocalPath(pm.LocalPath)
                })
                .OrderByDescending(x => x.Local.Length)
                .ThenBy(x => x.IsRoot ? 1 : 0)
                .ToList();

            var hasSubfolderMappings = ranked.Any(x => !x.IsRoot && !string.IsNullOrEmpty(x.Local));

            foreach (var item in ranked.Where(x => !x.IsRoot))
            {
                if (!IsUnderLocalMapping(relativeUnderMapping, item.Local))
                {
                    continue;
                }

                matchedMapping = item.Mapping;
                var prefix = item.Local + "/";
                if (relativeUnderMapping.Equals(item.Local, StringComparison.OrdinalIgnoreCase))
                {
                    relativeUnderMapping = string.Empty;
                }
                else if (relativeUnderMapping.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativeUnderMapping = relativeUnderMapping[prefix.Length..];
                }

                var mappedRemote = CombineRemotePaths(profileBase, item.Mapping.RemotePath);
                remoteFullPath = string.IsNullOrEmpty(relativeUnderMapping)
                    ? mappedRemote.TrimEnd('/')
                    : CombineRemotePaths(mappedRemote, relativeUnderMapping);
                return true;
            }

            // Fall back to an explicit project-root mapping if present.
            var rootMapping = ranked.FirstOrDefault(x => x.IsRoot);
            if (rootMapping != null)
            {
                matchedMapping = rootMapping.Mapping;
                var mappedRemote = CombineRemotePaths(profileBase, rootMapping.Mapping.RemotePath);
                remoteFullPath = string.IsNullOrEmpty(relativeUnderMapping)
                    ? mappedRemote.TrimEnd('/')
                    : CombineRemotePaths(mappedRemote, relativeUnderMapping);
                return true;
            }

            // Subfolder-only mappings (api + core): paths outside them must not upload.
            if (hasSubfolderMappings)
            {
                return false;
            }

            remoteFullPath = string.IsNullOrEmpty(relativeUnderMapping)
                ? profileBase.TrimEnd('/')
                : CombineRemotePaths(profileBase, relativeUnderMapping);
            return true;
        }

        /// <summary>
        /// Resolve upload target from an absolute local file path under the project root.
        /// </summary>
        public static bool TryResolveDeployTargetFromFullPath(
            string localFullPath,
            string projectRoot,
            IReadOnlyList<PathMapping>? mappings,
            string profileRemoteBase,
            out string remoteFullPath,
            out PathMapping? matchedMapping,
            out string relativeUnderMapping)
        {
            remoteFullPath = string.Empty;
            matchedMapping = null;
            relativeUnderMapping = string.Empty;

            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(localFullPath))
            {
                return false;
            }

            string relative;
            try
            {
                relative = Path.GetRelativePath(projectRoot, localFullPath).Replace("\\", "/");
            }
            catch
            {
                return false;
            }

            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            return TryResolveDeployTarget(
                relative,
                mappings,
                profileRemoteBase,
                out remoteFullPath,
                out matchedMapping,
                out relativeUnderMapping);
        }

        /// <summary>
        /// Stores a remote mapping relative to <paramref name="profileRemotePath"/> so
        /// <see cref="CombineRemotePaths"/> does not duplicate the profile root.
        /// </summary>
        public static string NormalizeStoredRemoteMapping(string? input, string? profileRemotePath)
        {
            var trimmed = (input ?? string.Empty).Trim().Replace("\\", "/");
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == ".")
            {
                return "/";
            }

            var profileRoot = NormalizeRemoteBase(profileRemotePath).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(profileRoot))
            {
                profileRoot = "/";
            }

            string absolute;
            if (trimmed.StartsWith('/'))
            {
                absolute = NormalizeRemoteBase(trimmed).TrimEnd('/');
            }
            else
            {
                return "/" + trimmed.Trim('/');
            }

            if (string.Equals(absolute, profileRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "/";
            }

            if (profileRoot != "/" &&
                absolute.StartsWith(profileRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = absolute[profileRoot.Length..].Trim('/');
                return string.IsNullOrWhiteSpace(relative) ? "/" : "/" + relative;
            }

            return string.IsNullOrWhiteSpace(absolute) ? "/" : NormalizeRemoteBase(absolute);
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

        /// <summary>
        /// Parent of a remote Unix path without using Windows <see cref="Path.GetDirectoryName"/>.
        /// </summary>
        public static string GetDirectoryPath(string remoteFileOrDirectoryPath)
        {
            return FtpDirectoryEnsure.GetParentDirectory(remoteFileOrDirectoryPath);
        }

        public static string GetRelativeRemotePath(string remoteRoot, string remotePath)
        {
            var normalizedRoot = EnsureTrailingSlash(remoteRoot).TrimEnd('/');
            var normalizedPath = NormalizeRemoteBase(remotePath).TrimEnd('/');
            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalizedPath[normalizedRoot.Length..].TrimStart('/');
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    return relative;
                }
            }

            return normalizedPath.TrimStart('/');
        }

        public static string ResolveLocalDownloadRoot(string? projectRoot, PathMapping? mapping, string? fallbackRoot = null)
        {
            var basePath = !string.IsNullOrWhiteSpace(projectRoot)
                ? projectRoot
                : fallbackRoot;

            if (string.IsNullOrWhiteSpace(basePath))
            {
                basePath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            if (mapping != null && !IsProjectRootLocalPath(mapping.LocalPath))
            {
                var localSegment = NormalizeLocalMappingPath(mapping.LocalPath)
                    .Replace("/", "\\")
                    .TrimStart('\\')
                    .Trim();
                if (!string.IsNullOrWhiteSpace(localSegment))
                {
                    basePath = Path.Combine(basePath, localSegment);
                }
            }

            return basePath;
        }

        public static string BuildLocalDownloadPath(
            string localRoot,
            string remoteRoot,
            string remotePath,
            bool isDirectory,
            string fallbackName)
        {
            var relative = GetRelativeRemotePath(remoteRoot, remotePath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                relative = fallbackName.Trim().Replace("/", "\\");
            }

            var normalizedRelative = relative.Replace("/", "\\").TrimStart('\\');
            var result = Path.Combine(localRoot, normalizedRelative);
            if (isDirectory)
            {
                return result.TrimEnd('\\', '/');
            }

            return result;
        }

        /// <summary>
        /// Resolve local download path from a remote path using all active mappings.
        /// Longer remote mapping segments win (mirror of upload local-segment ranking).
        /// </summary>
        public static bool TryResolveDownloadTargetFromRemotePath(
            string remotePath,
            ConnectionProfile? profile,
            string? projectRoot,
            bool isDirectory,
            string fallbackName,
            out string localFullPath,
            out PathMapping? matchedMapping)
        {
            localFullPath = string.Empty;
            matchedMapping = null;

            if (string.IsNullOrWhiteSpace(remotePath) || profile == null)
            {
                return false;
            }

            var mappings = GetActiveMappings(profile);
            var profileBase = NormalizeRemoteBase(profile.RemotePath).TrimEnd('/');

            if (mappings.Count == 0)
            {
                var localRoot = ResolveLocalDownloadRoot(projectRoot, null);
                localFullPath = BuildLocalDownloadPath(
                    localRoot,
                    profileBase,
                    remotePath,
                    isDirectory,
                    fallbackName);
                return true;
            }

            var ranked = mappings
                .Select(pm => new
                {
                    Mapping = pm,
                    MappedRemoteFull = CombineRemotePaths(profileBase, pm.RemotePath).TrimEnd('/'),
                    RemoteSegment = GetRemoteMappingSegment(profile.RemotePath, pm.RemotePath),
                    IsRoot = IsRootRemoteMapping(profile.RemotePath, pm.RemotePath)
                })
                .OrderByDescending(x => x.RemoteSegment.Length)
                .ThenBy(x => x.IsRoot ? 1 : 0)
                .ToList();

            var hasSubfolderMappings = ranked.Any(x => !x.IsRoot && !string.IsNullOrEmpty(x.RemoteSegment));

            foreach (var item in ranked.Where(x => !x.IsRoot))
            {
                if (!IsUnderRemoteMapping(remotePath, item.MappedRemoteFull))
                {
                    continue;
                }

                matchedMapping = item.Mapping;
                var localRoot = ResolveLocalDownloadRoot(projectRoot, item.Mapping);
                localFullPath = BuildLocalDownloadPath(
                    localRoot,
                    item.MappedRemoteFull,
                    remotePath,
                    isDirectory,
                    fallbackName);
                return true;
            }

            var rootMapping = ranked.FirstOrDefault(x => x.IsRoot);
            if (rootMapping != null && IsUnderRemoteMapping(remotePath, profileBase))
            {
                matchedMapping = rootMapping.Mapping;
                var localRoot = ResolveLocalDownloadRoot(projectRoot, rootMapping.Mapping);
                localFullPath = BuildLocalDownloadPath(
                    localRoot,
                    profileBase,
                    remotePath,
                    isDirectory,
                    fallbackName);
                return true;
            }

            if (hasSubfolderMappings)
            {
                return false;
            }

            if (IsUnderRemoteMapping(remotePath, profileBase))
            {
                var localRoot = ResolveLocalDownloadRoot(projectRoot, null);
                localFullPath = BuildLocalDownloadPath(
                    localRoot,
                    profileBase,
                    remotePath,
                    isDirectory,
                    fallbackName);
                return true;
            }

            return false;
        }

        private static string GetRemoteMappingSegment(string? profileRemotePath, string? storedRemoteMapping)
        {
            var normalizedStored = NormalizeStoredRemoteMapping(storedRemoteMapping, profileRemotePath);
            if (normalizedStored is "/" or "")
            {
                return string.Empty;
            }

            return normalizedStored.Trim('/').Trim();
        }

        private static bool IsRootRemoteMapping(string? profileRemotePath, string? storedRemoteMapping)
        {
            var normalizedStored = NormalizeStoredRemoteMapping(storedRemoteMapping, profileRemotePath);
            return normalizedStored is "/" or "";
        }

        private static bool IsUnderRemoteMapping(string remotePath, string mappedRemoteFull)
        {
            var path = NormalizeRemoteBase(remotePath).TrimEnd('/');
            var mapped = NormalizeRemoteBase(mappedRemoteFull).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(mapped) || mapped == "/")
            {
                return !string.IsNullOrWhiteSpace(path);
            }

            return path.Equals(mapped, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(mapped + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
