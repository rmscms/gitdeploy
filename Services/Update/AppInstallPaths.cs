using System;
using System.IO;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Stable per-user install location (no version in the EXE name).
    /// </summary>
    public static class AppInstallPaths
    {
        public const string ProductFolderName = "GitDeployPro";
        public const string ExecutableFileName = "GitDeployPro.exe";
        public const string DesktopShortcutFileName = "GitDeploy Pro.lnk";

        public static string InstallDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductFolderName);

        public static string ExecutablePath =>
            Path.Combine(InstallDirectory, ExecutableFileName);

        public static string UpdateStagingDirectory =>
            Path.Combine(InstallDirectory, "update");

        public static string PendingManifestPath =>
            Path.Combine(UpdateStagingDirectory, "pending.json");

        public static string PendingPackagePath =>
            Path.Combine(UpdateStagingDirectory, "package.exe");

        public static bool IsRunningFromInstallPath()
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(current))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(current),
                    Path.GetFullPath(ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
