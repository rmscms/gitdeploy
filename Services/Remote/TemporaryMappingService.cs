using System;
using System.Collections.Generic;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public sealed class TemporaryTarget
    {
        public string Name { get; init; } = "";
        public string RemotePath { get; init; } = "";

        public override string ToString() => $"{Name} → {RemotePath}";
    }

    /// <summary>
    /// Session-only mapping: ONE local folder → one or more named remote targets (e.g. site1, site2 …).
    /// While active it replaces the saved mappings of its profile (see <see cref="RemotePathResolver.GetActiveMappings"/>)
    /// using the *current* target. Direct Upload can run the same files against several targets in turn.
    /// Never written to config or manifest; saved mappings are not touched.
    /// </summary>
    public static class TemporaryMappingService
    {
        private static readonly List<TemporaryTarget> _targets = new();
        private static string _profileId = "";
        private static string _local = "/";
        private static int _current;

        /// <summary>Start / end / new session — views must reload (file tree, remote root).</summary>
        public static event EventHandler? Changed;

        /// <summary>Targets added or current target switched — only labels/banners need a refresh.</summary>
        public static event EventHandler? TargetsChanged;

        public static bool IsActive => _targets.Count > 0;

        public static string ProfileId => _profileId;

        public static string LocalPath => _local;

        public static IReadOnlyList<TemporaryTarget> Targets => _targets.ToList();

        public static int CurrentIndex => _current;

        public static bool HasMultipleTargets => _targets.Count > 1;

        public static TemporaryTarget? CurrentTarget => _targets.Count == 0 ? null : _targets[Math.Clamp(_current, 0, _targets.Count - 1)];

        public static PathMapping? Mapping => BuildMapping(CurrentTarget);

        public static PathMapping? GetFor(ConnectionProfile? profile)
        {
            if (!IsActive || profile == null)
            {
                return null;
            }

            return string.Equals(profile.Id, _profileId, StringComparison.Ordinal) ? Mapping : null;
        }

        private static PathMapping? BuildMapping(TemporaryTarget? target) => target == null
            ? null
            : new PathMapping
            {
                LocalPath = RemotePathResolver.NormalizeLocalMappingPath(_local),
                RemotePath = target.RemotePath,
                RemoteIsAbsolute = true
            };

        private static string Normalize(string remoteFull)
        {
            var remote = RemotePathResolver.NormalizeRemoteBase(remoteFull).TrimEnd('/');
            return string.IsNullOrEmpty(remote) ? "/" : remote;
        }

        private static string DefaultName(string remote)
        {
            var name = remote.TrimEnd('/').Split('/').LastOrDefault();
            return string.IsNullOrWhiteSpace(name) ? "/" : name;
        }

        /// <param name="localRelative">Folder relative to the project root ("/" = whole project).</param>
        /// <param name="remoteFull">Full server path of the first target.</param>
        public static void Start(ConnectionProfile profile, string localRelative, string remoteFull, string? name = null)
        {
            var remote = Normalize(remoteFull);
            _profileId = profile.Id;
            _local = localRelative;
            _targets.Clear();
            _targets.Add(new TemporaryTarget { Name = string.IsNullOrWhiteSpace(name) ? DefaultName(remote) : name.Trim(), RemotePath = remote });
            _current = 0;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Adds another remote target for the same local folder. False if it already exists.</summary>
        public static bool AddTarget(string remoteFull, string? name = null)
        {
            if (!IsActive)
            {
                return false;
            }

            var remote = Normalize(remoteFull);
            if (_targets.Any(t => string.Equals(t.RemotePath, remote, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _targets.Add(new TemporaryTarget { Name = string.IsNullOrWhiteSpace(name) ? DefaultName(remote) : name.Trim(), RemotePath = remote });
            TargetsChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>Switches the target used by path resolution without reloading views.</summary>
        public static void SetCurrent(int index, bool notify = true)
        {
            if (!IsActive)
            {
                return;
            }

            _current = Math.Clamp(index, 0, _targets.Count - 1);
            if (notify)
            {
                TargetsChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void End()
        {
            if (!IsActive)
            {
                return;
            }

            _targets.Clear();
            _profileId = "";
            _local = "/";
            _current = 0;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
