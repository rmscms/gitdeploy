using System;
using System.Collections.Generic;
using System.Linq;

namespace GitDeployPro.Models
{
    public static class ProjectFtpAssignments
    {
        public static IReadOnlyList<string> GetAssignedIds(ProjectConfig? config)
        {
            if (config == null)
            {
                return Array.Empty<string>();
            }

            if (config.ConnectionProfileIds != null && config.ConnectionProfileIds.Count > 0)
            {
                return config.ConnectionProfileIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(config.ConnectionProfileId))
            {
                return new[] { config.ConnectionProfileId.Trim() };
            }

            return Array.Empty<string>();
        }

        public static string GetDefaultId(ProjectConfig? config)
        {
            var assigned = GetAssignedIds(config);
            if (assigned.Count == 0)
            {
                return string.Empty;
            }

            if (config != null
                && !string.IsNullOrWhiteSpace(config.ConnectionProfileId)
                && assigned.Any(id => string.Equals(id, config.ConnectionProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                return config.ConnectionProfileId.Trim();
            }

            return assigned[0];
        }

        public static bool IsAssigned(ProjectConfig? config, string? profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            return GetAssignedIds(config)
                .Any(id => string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsDefault(ProjectConfig? config, string? profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            return string.Equals(GetDefaultId(config), profileId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static List<ConnectionProfile> ResolveAssignedProfiles(
            ProjectConfig? config,
            IEnumerable<ConnectionProfile>? profiles)
        {
            var byId = (profiles ?? Array.Empty<ConnectionProfile>())
                .Where(ConnectionProfileFilters.IsRemoteFileProfile)
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<ConnectionProfile>();
            var defaultId = GetDefaultId(config);
            foreach (var id in GetAssignedIds(config))
            {
                if (byId.TryGetValue(id, out var profile))
                {
                    result.Add(profile);
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultId)
                && byId.TryGetValue(defaultId, out var defaultProfile))
            {
                result.RemoveAll(p => string.Equals(p.Id, defaultId, StringComparison.OrdinalIgnoreCase));
                result.Insert(0, defaultProfile);
            }

            return result;
        }

        public static void Add(ProjectConfig config, string profileId)
        {
            if (config == null || string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            var id = profileId.Trim();
            var ids = GetAssignedIds(config).ToList();
            if (ids.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
            {
                config.ConnectionProfileIds = ids;
                return;
            }

            ids.Add(id);
            if (ids.Count == 1)
            {
                config.ConnectionProfileId = id;
                config.FtpSyncTargetConfirmed = true;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.ConnectionProfileId))
                {
                    config.ConnectionProfileId = id;
                }

                config.FtpSyncTargetConfirmed = false;
            }

            config.ConnectionProfileIds = ids;
        }

        public static void Remove(ProjectConfig config, string profileId)
        {
            if (config == null || string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            var ids = GetAssignedIds(config)
                .Where(id => !string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            config.ConnectionProfileIds = ids;

            if (string.Equals(config.ConnectionProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                || !ids.Any(id => string.Equals(id, config.ConnectionProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                config.ConnectionProfileId = ids.Count > 0 ? ids[0] : string.Empty;
            }

            if (ids.Count <= 1)
            {
                config.FtpSyncTargetConfirmed = true;
            }
        }

        public static void SetDefault(ProjectConfig config, string profileId, bool confirmed = true)
        {
            if (config == null || string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            var id = profileId.Trim();
            var ids = GetAssignedIds(config).ToList();
            ids.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
            ids.Insert(0, id);
            config.ConnectionProfileIds = ids;
            config.ConnectionProfileId = id;
            config.FtpSyncTargetConfirmed = confirmed;
        }

        public static void ApplySingle(ProjectConfig config, string profileId)
        {
            if (config == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(profileId))
            {
                config.ConnectionProfileId = string.Empty;
                config.ConnectionProfileIds = new List<string>();
                config.FtpSyncTargetConfirmed = true;
                return;
            }

            var id = profileId.Trim();
            config.ConnectionProfileId = id;
            config.ConnectionProfileIds = new List<string> { id };
            config.FtpSyncTargetConfirmed = true;
        }

        public static void CopyLegacyFields(ProjectConfig config, ConnectionProfile? profile)
        {
            if (config == null)
            {
                return;
            }

            if (profile == null)
            {
                config.FtpHost = string.Empty;
                config.FtpPort = 21;
                config.FtpUsername = string.Empty;
                config.FtpPassword = string.Empty;
                config.UseSSH = false;
                config.RemotePath = "/";
                return;
            }

            config.FtpHost = profile.Host ?? string.Empty;
            config.FtpPort = profile.Port > 0 ? profile.Port : 21;
            config.FtpUsername = profile.Username ?? string.Empty;
            config.FtpPassword = profile.Password ?? string.Empty;
            config.UseSSH = profile.UseSSH;
            config.RemotePath = string.IsNullOrWhiteSpace(profile.RemotePath) ? "/" : profile.RemotePath;
        }
    }
}
