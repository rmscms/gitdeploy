using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Behaviors;
using GitDeployPro.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GitDeployPro.Services.Remote
{
    public static class SyncManifestService
    {
        public const string ManifestFileName = ".gitdeploy.sync.json";

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string GetRemoteManifestPath(ConnectionProfile profile)
        {
            var root = RemotePathResolver.BuildRemoteRoot(profile).TrimEnd('/');
            return RemotePathResolver.CombineRemotePaths(root, ManifestFileName);
        }

        public static string NormalizeRemotePath(string? path)
        {
            var normalized = RemotePathResolver.NormalizeRemoteBase(path ?? string.Empty).TrimEnd('/');
            return string.IsNullOrWhiteSpace(normalized) ? "/" : normalized;
        }

        public static async Task<SyncManifestLoadResult> LoadAsync(
            IRemoteFileService remote,
            ConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (remote == null || profile == null)
            {
                return new SyncManifestLoadResult
                {
                    Found = false,
                    Manifest = new SyncManifest(),
                    ErrorMessage = "Not connected."
                };
            }

            var remotePath = GetRemoteManifestPath(profile);
            try
            {
                var json = await remote.ReadTextFileAsync(remotePath, cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new SyncManifestLoadResult { Found = true, Manifest = new SyncManifest() };
                }

                var manifest = JsonConvert.DeserializeObject<SyncManifest>(json, JsonSettings) ?? new SyncManifest();
                NormalizeManifest(manifest, profile);
                return new SyncManifestLoadResult { Found = true, Manifest = manifest };
            }
            catch (Exception ex) when (IsMissingRemoteFile(ex))
            {
                return new SyncManifestLoadResult { Found = false, Manifest = new SyncManifest() };
            }
            catch (Exception ex)
            {
                return new SyncManifestLoadResult
                {
                    Found = false,
                    Manifest = new SyncManifest(),
                    ErrorMessage = ex.Message
                };
            }
        }

        public static async Task SaveAsync(
            IRemoteFileService remote,
            ConnectionProfile profile,
            SyncManifest manifest,
            CancellationToken cancellationToken = default)
        {
            if (remote == null || profile == null)
            {
                throw new InvalidOperationException("Remote service or profile is missing.");
            }

            manifest.Version = SyncManifest.CurrentVersion;
            manifest.UpdatedUtc = AppTimeService.UtcNow;
            manifest.UpdatedBy = Environment.UserName;
            NormalizeManifest(manifest, profile);

            var json = JsonConvert.SerializeObject(manifest, JsonSettings);
            var remotePath = GetRemoteManifestPath(profile);
            await remote.UploadTextFileAsync(remotePath, json, progress: null, cancellationToken);
        }

        public static SyncManifest BuildFromCheckedNodes(
            IEnumerable<RemoteTreeNode> roots,
            ConnectionProfile profile,
            Func<RemoteTreeNode, bool>? excludeNode = null)
        {
            var manifest = new SyncManifest
            {
                Paths = CollectPathEntries(roots, excludeNode),
                Mappings = CloneMappings(profile)
            };
            NormalizeManifest(manifest, profile);
            return manifest;
        }

        public static void ApplyChecksToTree(SyncManifest manifest, IEnumerable<RemoteTreeNode> roots)
        {
            foreach (var root in roots)
            {
                root.ClearChecked();
            }

            if (manifest?.Paths == null || manifest.Paths.Count == 0)
            {
                return;
            }

            var wanted = manifest.Paths
                .Select(entry => NormalizeRemotePath(entry.Remote))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var node in EnumerateNodes(roots))
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                var path = NormalizeRemotePath(node.FullPath);
                if (wanted.Contains(path))
                {
                    node.IsChecked = true;
                }
            }
        }

        public static IReadOnlyList<RemoteTreeNode> ResolveDownloadNodes(
            IEnumerable<SyncManifestPathEntry> selectedPaths,
            IEnumerable<RemoteTreeNode> roots)
        {
            var nodes = new List<RemoteTreeNode>();
            var byPath = EnumerateNodes(roots)
                .Where(node => !node.IsPlaceholder)
                .GroupBy(node => NormalizeRemotePath(node.FullPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in selectedPaths.Where(entry => !string.IsNullOrWhiteSpace(entry.Remote)))
            {
                var normalized = NormalizeRemotePath(entry.Remote);
                if (byPath.TryGetValue(normalized, out var existing))
                {
                    nodes.Add(existing);
                    continue;
                }

                nodes.Add(CreateSyntheticNode(normalized, entry.IsDirectory));
            }

            return TreeMultiSelectHelpers.CollapseNestedByPath(
                nodes,
                node => node.FullPath,
                node => node.IsDirectory);
        }

        public static IList<SyncPathPreviewItem> BuildPreviewItems(SyncManifest manifest)
        {
            return manifest.Paths
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Remote))
                .Select(entry => new SyncPathPreviewItem
                {
                    Remote = NormalizeRemotePath(entry.Remote),
                    Kind = entry.IsDirectory ? "folder" : "file",
                    IsChecked = true
                })
                .ToList();
        }

        public static bool ShouldOfferMappingBootstrap(ConnectionProfile profile, SyncManifest manifest)
        {
            if (manifest.Mappings == null || manifest.Mappings.Count == 0)
            {
                return false;
            }

            var active = RemotePathResolver.GetActiveMappings(profile);
            return active.Count == 0;
        }

        public static void ApplyMappingsToProfile(
            ConnectionProfile profile,
            SyncManifest manifest,
            ConfigurationService configService)
        {
            profile.PathMappings = manifest.Mappings
                .Where(mapping => mapping != null)
                .Select(mapping => new PathMapping
                {
                    LocalPath = RemotePathResolver.NormalizeLocalMappingPath(mapping.LocalPath),
                    RemotePath = RemotePathResolver.NormalizeStoredRemoteMapping(mapping.RemotePath, profile.RemotePath)
                })
                .ToList();
            configService.AddOrUpdateConnection(profile);
        }

        private static List<SyncManifestPathEntry> CollectPathEntries(
            IEnumerable<RemoteTreeNode> roots,
            Func<RemoteTreeNode, bool>? excludeNode)
        {
            var folders = new List<RemoteTreeNode>();
            var files = new List<RemoteTreeNode>();
            CollectCheckedTargets(roots, folders, files, excludeNode);

            return folders
                .Select(node => new SyncManifestPathEntry
                {
                    Remote = NormalizeRemotePath(node.FullPath),
                    Kind = "folder"
                })
                .Concat(files.Select(node => new SyncManifestPathEntry
                {
                    Remote = NormalizeRemotePath(node.FullPath),
                    Kind = "file"
                }))
                .GroupBy(entry => entry.Remote, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Remote, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void CollectCheckedTargets(
            IEnumerable<RemoteTreeNode> nodes,
            ICollection<RemoteTreeNode> folders,
            ICollection<RemoteTreeNode> files,
            Func<RemoteTreeNode, bool>? excludeNode)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (excludeNode != null && excludeNode(node))
                {
                    if (node.IsDirectory)
                    {
                        var excludedChildren = node.Children.Where(child => !child.IsPlaceholder).ToList();
                        if (excludedChildren.Count > 0)
                        {
                            CollectCheckedTargets(excludedChildren, folders, files, excludeNode);
                        }
                    }

                    continue;
                }

                if (node.IsDirectory)
                {
                    var realChildren = node.Children.Where(child => !child.IsPlaceholder).ToList();
                    if (realChildren.Count > 0)
                    {
                        CollectCheckedTargets(realChildren, folders, files, excludeNode);
                    }
                    else if (node.IsChecked == true)
                    {
                        folders.Add(node);
                    }
                }
                else if (node.IsChecked == true)
                {
                    files.Add(node);
                }
            }
        }

        private static RemoteTreeNode CreateSyntheticNode(string remotePath, bool isDirectory)
        {
            var trimmed = remotePath.TrimEnd('/');
            var name = trimmed;
            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
            {
                name = trimmed[(lastSlash + 1)..];
            }
            else if (lastSlash == 0 && trimmed.Length > 1)
            {
                name = trimmed[1..];
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = trimmed;
            }

            return new RemoteTreeNode
            {
                Name = name,
                FullPath = isDirectory ? trimmed : remotePath,
                IsDirectory = isDirectory,
                SizeLabel = isDirectory ? "dir" : "file"
            };
        }

        private static List<PathMapping> CloneMappings(ConnectionProfile profile)
        {
            return RemotePathResolver.GetActiveMappings(profile)
                .Select(mapping => new PathMapping
                {
                    LocalPath = mapping.LocalPath ?? string.Empty,
                    RemotePath = mapping.RemotePath ?? string.Empty
                })
                .ToList();
        }

        private static void NormalizeManifest(SyncManifest manifest, ConnectionProfile profile)
        {
            manifest.Paths ??= new List<SyncManifestPathEntry>();
            manifest.Mappings ??= new List<PathMapping>();

            manifest.Paths = manifest.Paths
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Remote))
                .Select(entry => new SyncManifestPathEntry
                {
                    Remote = NormalizeRemotePath(entry.Remote),
                    Kind = entry.IsDirectory ? "folder" : "file"
                })
                .GroupBy(entry => entry.Remote, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Remote, StringComparer.OrdinalIgnoreCase)
                .ToList();

            manifest.Mappings = manifest.Mappings
                .Where(mapping => mapping != null)
                .Select(mapping => new PathMapping
                {
                    LocalPath = RemotePathResolver.NormalizeLocalMappingPath(mapping.LocalPath),
                    RemotePath = RemotePathResolver.NormalizeStoredRemoteMapping(mapping.RemotePath, profile.RemotePath)
                })
                .ToList();
        }

        private static IEnumerable<RemoteTreeNode> EnumerateNodes(IEnumerable<RemoteTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in EnumerateNodes(node.Children))
                {
                    yield return child;
                }
            }
        }

        private static bool IsMissingRemoteFile(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            return message.Contains("550", StringComparison.Ordinal)
                   || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        }
    }
}
