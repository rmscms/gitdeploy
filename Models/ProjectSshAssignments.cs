using System;
using System.Collections.Generic;
using System.Linq;

namespace GitDeployPro.Models
{
    public static class ProjectSshAssignments
    {
        public static string GetDefaultId(ProjectConfig? config, IEnumerable<ConnectionProfile>? profiles = null)
        {
            if (config == null)
            {
                return string.Empty;
            }

            var list = (profiles ?? Array.Empty<ConnectionProfile>()).ToList();
            if (!string.IsNullOrWhiteSpace(config.DefaultSshProfileId))
            {
                var explicitId = config.DefaultSshProfileId.Trim();
                if (list.Count == 0
                    || list.Any(p =>
                        string.Equals(p.Id, explicitId, StringComparison.OrdinalIgnoreCase)
                        && ConnectionProfileFilters.IsSshTerminalProfile(p)))
                {
                    return explicitId;
                }
            }

            var ftpDefaultId = ProjectFtpAssignments.GetDefaultId(config);
            if (string.IsNullOrWhiteSpace(ftpDefaultId))
            {
                return string.Empty;
            }

            if (list.Count == 0)
            {
                return ftpDefaultId;
            }

            var ftpProfile = list.FirstOrDefault(p =>
                string.Equals(p.Id, ftpDefaultId, StringComparison.OrdinalIgnoreCase));
            return ftpProfile != null && ConnectionProfileFilters.IsSshTerminalProfile(ftpProfile)
                ? ftpDefaultId
                : string.Empty;
        }

        public static ConnectionProfile? ResolveDefaultProfile(
            ProjectConfig? config,
            IEnumerable<ConnectionProfile>? profiles)
        {
            var id = GetDefaultId(config, profiles);
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return (profiles ?? Array.Empty<ConnectionProfile>())
                .FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                    && ConnectionProfileFilters.IsSshTerminalProfile(p));
        }

        public static void SetDefault(ProjectConfig config, string profileId)
        {
            if (config == null)
            {
                return;
            }

            config.DefaultSshProfileId = string.IsNullOrWhiteSpace(profileId)
                ? string.Empty
                : profileId.Trim();
        }
    }

}
