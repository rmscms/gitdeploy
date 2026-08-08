using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Windows
{
    public enum UpdateStatusPhase
    {
        Available,
        Downloading,
        Ready,
        Failed
    }

    /// <summary>
    /// Modeless update UI (does not block MainWindow). Owned by MainWindow.
    /// </summary>
    public partial class UpdateStatusWindow : Window
    {
        private UpdateStatusPhase _phase = UpdateStatusPhase.Available;
        private bool _allowClose;
        private bool _mandatory;

        public event Action? DownloadRequested;
        public event Action? RestartRequested;
        public event Action? CancelDownloadRequested;
        public event Action? Dismissed;

        public UpdateStatusPhase Phase => _phase;
        public UpdateManifest? Manifest { get; private set; }
        public PendingUpdateState? Pending { get; private set; }

        public UpdateStatusWindow()
        {
            InitializeComponent();
        }

        public void ShowAvailable(UpdateCheckResult result)
        {
            Manifest = result.Manifest;
            Pending = null;
            _mandatory = result.IsMandatory;
            _phase = UpdateStatusPhase.Available;

            var current = result.CurrentVersion?.ToString() ?? "?";
            var remote = result.RemoteVersion?.ToString() ?? result.Manifest?.Version ?? "?";

            HeaderIcon.Text = "⬆";
            TitleText.Text = _mandatory ? Loc.T("update.title.critical") : Loc.T("update.title.available");
            VersionText.Text = Loc.T("update.versionArrow", current, remote);
            DetailText.Text = _mandatory
                ? Loc.T("update.detail.critical")
                : Loc.T("update.detail.available");
            DetailText.Foreground = _mandatory
                ? (TryFindResource("Status.Warning") as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Orange)
                : (TryFindResource("Text.Secondary") as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Gray);

            BindChangelog(result.Manifest?.ResolveChangelogItems()
                          ?? (IReadOnlyList<string>)Array.Empty<string>(),
                result.Manifest?.ReleaseNotes);

            ProgressPanel.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = Loc.T("common.download");
            PrimaryButton.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = _mandatory ? Loc.T("common.exit") : Loc.T("common.later");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;

            Present();
        }

        public void ShowDownloading(UpdateManifest manifest, IReadOnlyList<string>? changelog = null)
        {
            Manifest = manifest;
            Pending = null;
            _mandatory = manifest.Mandatory;
            _phase = UpdateStatusPhase.Downloading;

            HeaderIcon.Text = "↓";
            TitleText.Text = Loc.T("update.title.downloading");
            VersionText.Text = Loc.T("update.version", manifest.Version);
            DetailText.Text = Loc.T("update.detail.downloading");
            DetailText.Foreground = TryFindResource("Text.Secondary") as System.Windows.Media.Brush
                                    ?? System.Windows.Media.Brushes.Gray;

            BindChangelog(changelog ?? manifest.ResolveChangelogItems(), manifest.ReleaseNotes);

            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            ProgressText.Text = Loc.T("update.starting");

            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = Loc.T("common.cancel");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;

            Present();
        }

        public void SetDownloadProgress(double percent)
        {
            if (_phase != UpdateStatusPhase.Downloading)
            {
                return;
            }

            if (percent < 0)
            {
                ProgressBar.IsIndeterminate = true;
                ProgressText.Text = Loc.T("update.downloading");
                return;
            }

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(percent, 0, 100);
            ProgressText.Text = Loc.T("update.downloadingPct", percent.ToString("0"));
        }

        public void ShowReady(PendingUpdateState pending)
        {
            Pending = pending;
            Manifest = null;
            _mandatory = pending.Mandatory;
            _phase = UpdateStatusPhase.Ready;

            HeaderIcon.Text = "✓";
            TitleText.Text = Loc.T("update.title.ready");
            VersionText.Text = Loc.T("update.version", pending.Version);
            DetailText.Text = Loc.T("update.detail.ready");
            DetailText.Foreground = TryFindResource("Status.Success") as System.Windows.Media.Brush
                                    ?? System.Windows.Media.Brushes.SeaGreen;

            BindChangelog(pending.ResolveChangelogItems(), pending.ReleaseNotes);

            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            ProgressText.Text = Loc.T("update.complete");

            PrimaryButton.Content = Loc.T("common.restart");
            PrimaryButton.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = Loc.T("common.later");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;

            Present();
        }

        public void ShowFailed(string message, UpdateManifest? manifest = null)
        {
            if (manifest != null)
            {
                Manifest = manifest;
            }

            _phase = UpdateStatusPhase.Failed;
            HeaderIcon.Text = "!";
            TitleText.Text = Loc.T("update.title.failed");
            VersionText.Text = Manifest != null ? Loc.T("update.version", Manifest.Version) : "";
            DetailText.Text = string.IsNullOrWhiteSpace(message) ? Loc.T("update.title.failed") : message;
            DetailText.Foreground = TryFindResource("Status.Error") as System.Windows.Media.Brush
                                    ?? System.Windows.Media.Brushes.IndianRed;

            ProgressPanel.Visibility = Visibility.Collapsed;
            PrimaryButton.Visibility = Visibility.Collapsed;
            SecondaryButton.Content = Loc.T("common.dismiss");
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsEnabled = true;

            Present();
        }

        public void SetBusyInstalling()
        {
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
            ProgressText.Text = Loc.T("update.installing");
        }

        public void ResetBusyAfterInstallFailure()
        {
            PrimaryButton.IsEnabled = true;
            SecondaryButton.IsEnabled = true;
        }

        private void BindChangelog(IReadOnlyList<string> items, string? releaseNotesFallback)
        {
            var list = items?
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList() ?? new List<string>();

            if (list.Count == 0 && !string.IsNullOrWhiteSpace(releaseNotesFallback))
            {
                list = releaseNotesFallback
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().TrimStart('-', '*', '•', ' '))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
            }

            if (list.Count == 0)
            {
                list.Add(Loc.T("update.noNotes"));
            }

            ChangelogList.ItemsSource = list;
        }

        private void Present()
        {
            if (!IsVisible)
            {
                Show();
            }

            Activate();
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_phase)
            {
                case UpdateStatusPhase.Available:
                    DownloadRequested?.Invoke();
                    break;
                case UpdateStatusPhase.Ready:
                    RestartRequested?.Invoke();
                    break;
            }
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_phase)
            {
                case UpdateStatusPhase.Available:
                    if (_mandatory)
                    {
                        _allowClose = true;
                        System.Windows.Application.Current?.Shutdown();
                        return;
                    }

                    SoftClose();
                    break;
                case UpdateStatusPhase.Downloading:
                    CancelDownloadRequested?.Invoke();
                    break;
                case UpdateStatusPhase.Ready:
                    SoftClose();
                    break;
                case UpdateStatusPhase.Failed:
                    SoftClose();
                    break;
            }
        }

        private void SoftClose()
        {
            _allowClose = true;
            Dismissed?.Invoke();
            Hide();
            _allowClose = false;
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowClose)
            {
                return;
            }

            // Hide instead of destroy so MainWindow can reuse the instance; app keeps working.
            e.Cancel = true;
            if (_phase == UpdateStatusPhase.Downloading)
            {
                // Closing while downloading = cancel request (same as Cancel).
                CancelDownloadRequested?.Invoke();
                return;
            }

            if (_phase == UpdateStatusPhase.Available && _mandatory)
            {
                System.Windows.Application.Current?.Shutdown();
                return;
            }

            SoftClose();
        }
    }
}
