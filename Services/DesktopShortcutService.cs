using System;
using System.IO;
using System.Runtime.InteropServices;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Services
{
    /// <summary>
    /// Creates/updates a Desktop .lnk that points at the stable install EXE.
    /// </summary>
    public static class DesktopShortcutService
    {
        public static string GetDesktopShortcutPath()
        {
            var desktop = ResolveDesktopDirectory();
            return Path.Combine(desktop, AppInstallPaths.DesktopShortcutFileName);
        }

        /// <summary>
        /// If the stable install EXE exists and the Desktop shortcut is missing or points elsewhere,
        /// create/update <c>GitDeploy Pro.lnk</c>. Covers older portable users after migrate/update.
        /// </summary>
        public static void EnsureDefaultShortcut()
        {
            var targetExe = TryResolveInstallExecutable();
            if (targetExe == null)
            {
                return;
            }

            EnsureShortcut(targetExe);
        }

        /// <summary>
        /// Ensures Desktop shortcut exists and targets <paramref name="targetExe"/>.
        /// Does not delete old versioned EXE files on the Desktop.
        /// </summary>
        public static void EnsureShortcut(string targetExe)
        {
            if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
            {
                return;
            }

            var shortcutPath = GetDesktopShortcutPath();
            var workingDir = Path.GetDirectoryName(targetExe) ?? string.Empty;

            try
            {
                if (File.Exists(shortcutPath) && ShortcutAlreadyPointsTo(shortcutPath, targetExe))
                {
                    return;
                }

                CreateShortcut(shortcutPath, targetExe, workingDir);
            }
            catch
            {
                // Best-effort; missing COM / desktop permissions must not break the app.
            }
        }

        private static string? TryResolveInstallExecutable()
        {
            try
            {
                if (File.Exists(AppInstallPaths.ExecutablePath))
                {
                    return Path.GetFullPath(AppInstallPaths.ExecutablePath);
                }

                var defaultExe = Path.Combine(
                    AppInstallPaths.DefaultInstallDirectory,
                    AppInstallPaths.ExecutableFileName);
                if (File.Exists(defaultExe))
                {
                    return Path.GetFullPath(defaultExe);
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ResolveDesktopDirectory()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            {
                return desktop;
            }

            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            {
                return desktop;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static bool ShortcutAlreadyPointsTo(string shortcutPath, string targetExe)
        {
            try
            {
                var existing = ResolveShortcutTarget(shortcutPath);
                if (string.IsNullOrWhiteSpace(existing))
                {
                    return false;
                }

                return string.Equals(
                    Path.GetFullPath(existing),
                    Path.GetFullPath(targetExe),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create WScript.Shell.");
            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = workingDir;
                shortcut.IconLocation = targetExe;
                shortcut.Description = "GitDeploy Pro";
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static string ResolveShortcutTarget(string shortcutPath)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return string.Empty;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return string.Empty;
            }

            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                try
                {
                    return shortcut.TargetPath as string ?? string.Empty;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
