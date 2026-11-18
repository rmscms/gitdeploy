using System;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    /// <summary>
    /// Session-only local→remote mapping. While active it replaces the saved mappings of its profile
    /// (see <see cref="RemotePathResolver.GetActiveMappings"/>). Never written to config or manifest.
    /// </summary>
    public static class TemporaryMappingService
    {
        private static PathMapping? _mapping;
        private static string _profileId = "";

        public static event EventHandler? Changed;

        public static bool IsActive => _mapping != null;

        public static PathMapping? Mapping => _mapping;

        public static string ProfileId => _profileId;

        public static PathMapping? GetFor(ConnectionProfile? profile)
        {
            if (_mapping == null || profile == null)
            {
                return null;
            }

            return string.Equals(profile.Id, _profileId, StringComparison.Ordinal) ? _mapping : null;
        }

        /// <param name="localRelative">Folder relative to the project root ("/" = whole project).</param>
        /// <param name="remoteFull">Full server path of the target folder.</param>
        public static void Start(ConnectionProfile profile, string localRelative, string remoteFull)
        {
            var remote = RemotePathResolver.NormalizeRemoteBase(remoteFull).TrimEnd('/');
            if (string.IsNullOrEmpty(remote))
            {
                remote = "/";
            }

            _profileId = profile.Id;
            _mapping = new PathMapping
            {
                LocalPath = RemotePathResolver.NormalizeLocalMappingPath(localRelative),
                RemotePath = remote,
                RemoteIsAbsolute = true
            };
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void End()
        {
            if (_mapping == null)
            {
                return;
            }

            _mapping = null;
            _profileId = "";
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
