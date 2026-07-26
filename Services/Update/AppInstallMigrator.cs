using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GitDeployPro.Controls;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Moves portable/Desktop launches into the stable LocalAppData install home.
    /// </summary>
    public static class AppInstallMigrator
    {
        /// <summary>
        /// If the process is not running from the install path, migrate and relaunch.
        /// Returns true when the current process should shut down.
        /// </summary>
        public static bool TryMigrateAndRelaunchIfNeeded()
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                return false;
            }

            if (AppInstallPaths.IsRunningFromInstallPath())
            {
                DesktopShortcutService.EnsureShortcut(AppInstallPaths.ExecutablePath);
                RefreshAutoStartIfEnabled(AppInstallPaths.ExecutablePath);
                return false;
            }

            if (ShouldSkipMigration(currentExe))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(AppInstallPaths.InstallDirectory);
                var installExe = AppInstallPaths.ExecutablePath;
                var currentVersion = new AppUpdateService().GetCurrentVersion();
                var installVersion = TryReadFileVersion(installExe);

                var shouldCopy = !File.Exists(installExe) || installVersion == null || installVersion < currentVersion;
                if (shouldCopy)
                {
                    File.Copy(currentExe, installExe, overwrite: true);
                }

                DesktopShortcutService.EnsureShortcut(installExe);
                RefreshAutoStartIfEnabled(installExe);

                var config = new ConfigurationService();
                var global = config.LoadGlobalConfig();
                if (!global.HasShownInstallMigrationNotice)
                {
                    config.UpdateGlobalConfig(cfg => cfg.HasShownInstallMigrationNotice = true);
                    try
                    {
                        ModernMessageBox.Show(
                            "GitDeploy Pro is now installed under LocalAppData.\n\n" +
                            $"Install folder:\n{AppInstallPaths.InstallDirectory}\n\n" +
                            "A Desktop shortcut named \"GitDeploy Pro\" was created.\n" +
                            "Old versioned EXE files on the Desktop are leftovers and can be deleted manually.",
                            "Installed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch
                    {
                    }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = installExe,
                    UseShellExecute = true,
                    WorkingDirectory = AppInstallPaths.InstallDirectory
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RefreshAutoStartIfEnabled(string exePath)
        {
            try
            {
                var config = new ConfigurationService().LoadGlobalConfig();
                var autoStart = new AutoStartService();
                if (config.LaunchOnStartup || autoStart.IsEnabled())
                {
                    autoStart.SetAutoStart(true, exePath);
                }
            }
            catch
            {
            }
        }

        private static bool ShouldSkipMigration(string currentExe)
        {
            try
            {
                var full = Path.GetFullPath(currentExe);
                if (Debugger.IsAttached)
                {
                    return true;
                }

                if (full.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    full.Contains(".tmp_build", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static Version? TryReadFileVersion(string exePath)
        {
            try
            {
                if (!File.Exists(exePath))
                {
                    return null;
                }

                var info = FileVersionInfo.GetVersionInfo(exePath);
                var raw = info.ProductVersion ?? info.FileVersion;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                if (Version.TryParse(AppUpdateService.NormalizeVersionString(raw), out var version))
                {
                    return version;
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
