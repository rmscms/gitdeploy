using System;
using System.IO;
using Microsoft.Win32;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Stable per-user install location (no version in the EXE name).
    /// Chosen directory is persisted in HKCU for discovery across portable launches.
    /// </summary>
    public static class AppInstallPaths
    {
        public const string ProductFolderName = "GitDeployPro";
        public const string ExecutableFileName = "GitDeployPro.exe";
        public const string DesktopShortcutFileName = "GitDeploy Pro.lnk";
        public const string RegistryKeyPath = @"Software\GitDeployPro";
        public const string RegistryInstallDirectoryValue = "InstallDirectory";

        /// <summary>Rough minimum free space required before copying the self-contained EXE.</summary>
        public const long MinimumFreeBytes = 220L * 1024 * 1024;

        public static string DefaultInstallDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductFolderName);

        public static string InstallDirectory
        {
            get
            {
                var saved = TryReadRegistryInstallDirectory();
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    return saved;
                }

                return DefaultInstallDirectory;
            }
        }

        public static string ExecutablePath =>
            Path.Combine(InstallDirectory, ExecutableFileName);

        public static string UpdateStagingDirectory =>
            Path.Combine(InstallDirectory, "update");

        public static string PendingManifestPath =>
            Path.Combine(UpdateStagingDirectory, "pending.json");

        public static string PendingPackagePath =>
            Path.Combine(UpdateStagingDirectory, "package.exe");

        public static string WhatsNewPath =>
            Path.Combine(UpdateStagingDirectory, "whats-new.json");

        public static bool HasRegisteredInstall()
        {
            var dir = TryReadRegistryInstallDirectory();
            if (string.IsNullOrWhiteSpace(dir))
            {
                return false;
            }

            return File.Exists(Path.Combine(dir, ExecutableFileName));
        }

        public static string? TryReadRegistryInstallDirectory()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
                var value = key?.GetValue(RegistryInstallDirectoryValue) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return Path.GetFullPath(value.Trim());
            }
            catch
            {
                return null;
            }
        }

        public static void SetInstallDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Install directory is required.", nameof(directory));
            }

            var full = Path.GetFullPath(directory.Trim());
            Directory.CreateDirectory(full);

            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath)
                ?? throw new InvalidOperationException("Could not open HKCU\\Software\\GitDeployPro.");
            key.SetValue(RegistryInstallDirectoryValue, full);

            try
            {
                var configService = new ConfigurationService();
                configService.UpdateGlobalConfig(cfg => cfg.InstallDirectory = full);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Builds the final product folder from a Browse selection.
        /// If the user already selected a folder named GitDeployPro, use it as-is.
        /// </summary>
        public static string ResolveProductInstallDirectory(string selectedPath)
        {
            var full = Path.GetFullPath((selectedPath ?? string.Empty).Trim());
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(name, ProductFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            return Path.Combine(full, ProductFolderName);
        }

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

        public static bool TryGetDriveFreeBytes(string directory, out long freeBytes)
        {
            freeBytes = 0;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return false;
                }

                freeBytes = drive.AvailableFreeSpace;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }
    }
}
