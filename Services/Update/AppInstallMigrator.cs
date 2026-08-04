using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Windows;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// First-install location picker + portable → chosen install folder migration.
    /// </summary>
    public static class AppInstallMigrator
    {
        /// <summary>
        /// If the process is not running from the install path, install/migrate and relaunch.
        /// Returns true when the current process should shut down.
        /// </summary>
        public static bool TryMigrateAndRelaunchIfNeeded()
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                return false;
            }

            if (ShouldSkipMigration(currentExe))
            {
                return false;
            }

            // Seamless upgrade: previous LocalAppData install without registry → register it.
            EnsureLegacyDefaultInstallRegistered();

            // Always heal Desktop shortcut when a stable install EXE exists (older portable users).
            DesktopShortcutService.EnsureDefaultShortcut();

            if (AppInstallPaths.IsRunningFromInstallPath())
            {
                RefreshAutoStartIfEnabled(AppInstallPaths.ExecutablePath);
                return false;
            }

            // Already installed elsewhere: relaunch that copy (refresh if portable is newer).
            if (AppInstallPaths.HasRegisteredInstall())
            {
                return RelaunchFromRegisteredInstall(currentExe);
            }

            // First install for this Windows user → choose location.
            InstallLocationWindow dialog;
            try
            {
                dialog = new InstallLocationWindow();
                dialog.ShowDialog();
            }
            catch
            {
                return false;
            }

            if (dialog.Choice != InstallLocationChoice.Install)
            {
                // Run portable: do not write registry.
                return false;
            }

            try
            {
                return InstallToDirectoryAndRelaunch(currentExe, dialog.SelectedInstallDirectory, showNotice: true);
            }
            catch (Exception ex)
            {
                try
                {
                    ModernMessageBox.Show(
                        $"Install failed:\n{ex.Message}",
                        "Install",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                }

                return false;
            }
        }

        private static void EnsureLegacyDefaultInstallRegistered()
        {
            if (AppInstallPaths.HasRegisteredInstall())
            {
                return;
            }

            var legacyExe = Path.Combine(AppInstallPaths.DefaultInstallDirectory, AppInstallPaths.ExecutableFileName);
            if (File.Exists(legacyExe))
            {
                AppInstallPaths.SetInstallDirectory(AppInstallPaths.DefaultInstallDirectory);
                DesktopShortcutService.EnsureShortcut(legacyExe);
            }
        }

        private static bool RelaunchFromRegisteredInstall(string currentExe)
        {
            try
            {
                var installExe = AppInstallPaths.ExecutablePath;
                var currentVersion = new AppUpdateService().GetCurrentVersion();
                var installVersion = TryReadFileVersion(installExe);
                if (installVersion == null || installVersion < currentVersion)
                {
                    Directory.CreateDirectory(AppInstallPaths.InstallDirectory);
                    File.Copy(currentExe, installExe, overwrite: true);
                    CopyCompanionAssets(currentExe, AppInstallPaths.InstallDirectory);
                }

                DesktopShortcutService.EnsureShortcut(installExe);
                RefreshAutoStartIfEnabled(installExe);
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

        private static bool InstallToDirectoryAndRelaunch(string currentExe, string installDirectory, bool showNotice)
        {
            AppInstallPaths.SetInstallDirectory(installDirectory);
            Directory.CreateDirectory(AppInstallPaths.InstallDirectory);

            var installExe = AppInstallPaths.ExecutablePath;
            File.Copy(currentExe, installExe, overwrite: true);
            CopyCompanionAssets(currentExe, AppInstallPaths.InstallDirectory);

            DesktopShortcutService.EnsureShortcut(installExe);
            RefreshAutoStartIfEnabled(installExe);

            if (showNotice)
            {
                var config = new ConfigurationService();
                var global = config.LoadGlobalConfig();
                if (!global.HasShownInstallMigrationNotice)
                {
                    config.UpdateGlobalConfig(cfg =>
                    {
                        cfg.HasShownInstallMigrationNotice = true;
                        cfg.InstallDirectory = AppInstallPaths.InstallDirectory;
                    });
                    try
                    {
                        ModernMessageBox.Show(
                            "GitDeploy Pro is installed.\n\n" +
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
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installExe,
                UseShellExecute = true,
                WorkingDirectory = AppInstallPaths.InstallDirectory
            });
            return true;
        }

        /// <summary>
        /// Copies optional on-disk companions (Themes/Packs) next to the install EXE when present
        /// beside the source portable/publish folder. Themes still seed into %AppData% from code.
        /// </summary>
        private static void CopyCompanionAssets(string sourceExe, string installDirectory)
        {
            try
            {
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceExe));
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    return;
                }

                var themesSrc = Path.Combine(sourceDir, "Themes");
                if (!Directory.Exists(themesSrc))
                {
                    return;
                }

                CopyDirectory(themesSrc, Path.Combine(installDirectory, "Themes"));
            }
            catch
            {
                // Best-effort; missing packs still work via ThemeTokenCatalog → AppData seed.
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
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
