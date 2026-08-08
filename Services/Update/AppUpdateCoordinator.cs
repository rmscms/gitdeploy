using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services.Localization;
using GitDeployPro.Windows;

namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Shared UI flow for automatic and manual update checks.
    /// </summary>
    public static class AppUpdateCoordinator
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);

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
                if (owner is MainWindow busyMain && busyMain.IsBackgroundUpdateInProgress)
                {
                    busyMain.ActivateUpdateStatusWindow();
                    if (showUpToDateMessage)
                    {
                        ModernMessageBox.Show(
                            Loc.T("msg.updateInProgress"),
                            Loc.T("settings.updates"),
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

                // If a package is already downloaded, show the modeless ready UI.
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
                            Loc.T("msg.updateAlreadyDownloaded", pending.Version),
                            Loc.T("settings.updates"),
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
                            Loc.T("msg.updateCheckFailed", ex.Message),
                            Loc.T("settings.updates"),
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
                            Loc.T("msg.updateCheckFailed", result.Error),
                            Loc.T("settings.updates"),
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
                            Loc.T("msg.upToDate", service.GetCurrentVersion()),
                            Loc.T("settings.updates"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            owner: owner);
                    }

                    return;
                }

                if (owner is MainWindow mainWindow)
                {
                    // Non-blocking: main window stays usable while the update modal is open.
                    mainWindow.ShowUpdateAvailable(result);
                    return;
                }

                // Fallback for non-main owners: classic dialog + progress.
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
            finally
            {
                Gate.Release();
            }
        }
    }
}
