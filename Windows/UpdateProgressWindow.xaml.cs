using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Windows
{
    public partial class UpdateProgressWindow : Window
    {
        private readonly AppUpdateService _updateService;
        private readonly UpdateManifest _manifest;
        private readonly CancellationTokenSource _cts = new();
        private bool _started;

        public bool ApplyStarted { get; private set; }

        public UpdateProgressWindow(AppUpdateService updateService, UpdateManifest manifest)
        {
            InitializeComponent();
            _updateService = updateService;
            _manifest = manifest;
            Loaded += UpdateProgressWindow_Loaded;
            Closing += (_, e) =>
            {
                if (!ApplyStarted)
                {
                    _cts.Cancel();
                }
            };
        }

        private async void UpdateProgressWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            try
            {
                var progress = new Progress<double>(value =>
                {
                    if (value < 0)
                    {
                        DownloadProgress.IsIndeterminate = true;
                        StatusText.Text = "Downloading...";
                        return;
                    }

                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = value;
                    StatusText.Text = $"Downloading... {value:0}%";
                });

                await _updateService.DownloadAndApplyAsync(_manifest, progress, _cts.Token);
                ApplyStarted = true;
                StatusText.Text = "Installing update and restarting...";
                await Task.Delay(400);
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                DialogResult = false;
                Close();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Update failed:\n{ex.Message}",
                    "Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    owner: this);
                DialogResult = false;
                Close();
            }
        }
    }
}
