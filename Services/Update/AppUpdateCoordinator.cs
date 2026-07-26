using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Windows;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Shared UI flow for automatic and manual update checks.
    /// </summary>
    public static class AppUpdateCoordinator
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static bool _dialogOpen;

        public static async Task RunAutomaticCheckAsync(Window? owner)
        {
            if (!UpdateOptions.IsConfigured)
            {
                return;
            }

            var service = new AppUpdateService();
            if (!service.ShouldCheckAutomatically())
            {
                // Still surface a previously downloaded pending update.
                if (owner is MainWindow mainWindow)
                {
                    mainWindow.RestorePendingUpdateFooterIfAny();
                }

                return;
            }

            await RunCheckInternalAsync(owner, service, showUpToDateMessage: false, isManual: false);
        }

        public static async Task RunManualCheckAsync(Window? owner)
        {
            var service = new AppUpdateService();
            await RunCheckInternalAsync(owner, service, showUpToDateMessage: true, isManual: true);
        }

        private static async Task RunCheckInternalAsync(
            Window? owner,
            AppUpdateService service,
            bool showUpToDateMessage,
            bool isManual)
        {
            if (!await Gate.WaitAsync(0))
            {
                return;
            }

            try
            {
                if (_dialogOpen)
                {
                    return;
                }

                if (owner is MainWindow busyMain && busyMain.IsBackgroundUpdateInProgress)
                {
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            "An update download is already in progress.",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            owner: owner);
                    }

                    return;
                }

                if (!UpdateOptions.IsConfigured)
                {
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            "Update server is not configured for this app build.\nSet UpdateOptions.BaseUrl in code.",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            owner: owner);
                    }

                    return;
                }

                // If a package is already downloaded, just show the footer.
                var pending = service.GetPendingUpdate();
                if (pending != null &&
                    Version.TryParse(AppUpdateService.NormalizeVersionString(pending.Version), out var pendingVer) &&
                    pendingVer > service.GetCurrentVersion())
                {
                    if (owner is MainWindow mainWithPending)
                    {
                        mainWithPending.ShowUpdateReadyFooter(pending);
                    }

                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            $"Update {pending.Version} is already downloaded.\nUse Restart now in the footer to apply it.",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            owner: owner);
                    }

                    return;
                }

                UpdateCheckResult result;
                try
                {
                    result = await service.CheckForUpdateAsync();
                }
                catch (Exception ex)
                {
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            $"Could not check for updates:\n{ex.Message}",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            owner: owner);
                    }

                    return;
                }

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            $"Could not check for updates:\n{result.Error}",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            owner: owner);
                    }

                    return;
                }

                if (!result.IsUpdateAvailable || result.Manifest == null)
                {
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            $"You are on the latest version ({service.GetCurrentVersion()}).",
                            "Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            owner: owner);
                    }

                    return;
                }

                _dialogOpen = true;
                try
                {
                    var prompt = new UpdateAvailableWindow(result)
                    {
                        Owner = owner
                    };
                    prompt.ShowDialog();

                    if (prompt.Choice == UpdateDialogChoice.Exit)
                    {
                        System.Windows.Application.Current?.Shutdown();
                        return;
                    }

                    if (prompt.Choice != UpdateDialogChoice.UpdateNow)
                    {
                        return;
                    }

                    if (owner is MainWindow mainWindow)
                    {
                        mainWindow.StartBackgroundUpdateDownload(result.Manifest);
                    }
                    else
                    {
                        // Fallback for non-main owners: blocking progress window.
                        var progress = new UpdateProgressWindow(service, result.Manifest)
                        {
                            Owner = owner
                        };
                        var applied = progress.ShowDialog() == true && progress.ApplyStarted;
                        if (applied)
                        {
                            System.Windows.Application.Current?.Shutdown();
                        }
                    }
                }
                finally
                {
                    _dialogOpen = false;
                }
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
