using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GitDeployPro.Services.Telegram
{
    public static class TelegramPaths
    {
        public const string UnassignedPath = "__unassigned__";

        public static string RootFolder
        {
            get
            {
                var root = Path.Combine(new ConfigurationService().GetAppDataFolder(), "telegram");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        public static string ThreadsFolder
        {
            get
            {
                var folder = Path.Combine(RootFolder, "threads");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public static string StateFile => Path.Combine(RootFolder, "state.json");

        public static string MediaFolder(string projectPath)
        {
            var folder = Path.Combine(RootFolder, "media", ToProjectKey(projectPath));
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string ThreadFile(string projectPath)
            => Path.Combine(ThreadsFolder, ToProjectKey(projectPath) + ".json");

        public static bool IsUnassigned(string? projectPath)
            => string.IsNullOrWhiteSpace(projectPath)
               || string.Equals(projectPath.Trim(), UnassignedPath, StringComparison.OrdinalIgnoreCase);

        public static string ToProjectKey(string? projectPath)
        {
            if (IsUnassigned(projectPath))
            {
                return "unassigned";
            }

            string full;
            try
            {
                full = Path.GetFullPath(projectPath!.Trim());
            }
            catch
            {
                full = projectPath!.Trim();
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))
                .ToLowerInvariant();
            return hash[..16];
        }

        public static string DisplayName(string? projectPath)
        {
            if (IsUnassigned(projectPath))
            {
                return Localization.Loc.T("telegram.unassigned");
            }

            try
            {
                var name = Path.GetFileName(projectPath!.Trim());
                return string.IsNullOrWhiteSpace(name) ? projectPath : name;
            }
            catch
            {
                return projectPath ?? Localization.Loc.T("telegram.unassigned");
            }
        }
    }
}
