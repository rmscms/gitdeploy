using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class SshKeySetupWindow
    {
        private readonly SshAgentService _sshAgentService = new();
        private readonly string _remoteUrl;
        private string _host;

        public SshKeySetupWindow(string remoteUrl)
        {
            InitializeComponent();
            _remoteUrl = remoteUrl ?? string.Empty;
            _host = SshAgentService.ResolveSshHost(_remoteUrl);
        }

        public static bool ShowForRemote(DependencyObject? owner, string remoteUrl)
        {
            var window = new SshKeySetupWindow(remoteUrl);
            return WindowOwnerService.ShowDialogOwned(window, owner) == true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshFromDisk();
        }

        private void RefreshFromDisk()
        {
            var status = _sshAgentService.ProbeLocalState(_remoteUrl);
            _host = status.Host;
            KeyPathTextBox.Text = status.KeyPath ?? string.Empty;
            PublicKeyTextBox.Text = status.PublicKey ?? string.Empty;
            StatusText.Text = status.Message;
            DetailsText.Text = BuildDetails(status);
            OpenHostKeysButton.Content = HostKeysButtonLabel(_host);
            HostKeysHintText.Text = SshAgentService.GetKeysSettingsUrl(_host);
            TestResultText.Text = string.Empty;
            TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Secondary");
        }

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true);
                var comment = $"{Environment.UserName}@gitdeploypro";
                var keyPath = await _sshAgentService.GenerateEd25519KeyAsync(comment);
                KeyPathTextBox.Text = keyPath;
                PublicKeyTextBox.Text = SshAgentService.ReadPublicKey(keyPath) ?? string.Empty;
                StatusText.Text = $"Generated key: {keyPath}";
                DetailsText.Text = "Copy the public key to the host, then Test connection.";
                TestResultText.Text = "Key created. Add it on the host before testing.";
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Info");
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Generate SSH key", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select SSH private key",
                CheckFileExists = true,
                Filter = "SSH private key|id_*;*.pem;*.key|All files|*.*"
            };
            if (Directory.Exists(sshDir))
            {
                dialog.InitialDirectory = sshDir;
            }

            if (dialog.ShowDialog(this) == true)
            {
                KeyPathTextBox.Text = dialog.FileName;
                PublicKeyTextBox.Text = SshAgentService.ReadPublicKey(dialog.FileName) ?? string.Empty;
                _sshAgentService.RememberKeyPath(dialog.FileName);
                StatusText.Text = $"Using key: {dialog.FileName}";
            }
        }

        private void CopyPublicKeyButton_Click(object sender, RoutedEventArgs e)
        {
            var pub = (PublicKeyTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pub))
            {
                ModernMessageBox.Show("No public key found next to the private key.", "Copy public key", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(pub);
                TestResultText.Text = "Public key copied. Paste it on the host SSH keys page.";
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Success");
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Copy public key", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenHostKeysButton_Click(object sender, RoutedEventArgs e)
        {
            var url = SshAgentService.GetKeysSettingsUrl(_host);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not open {url}\n{ex.Message}", "SSH keys page", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true);
                var keyPath = (KeyPathTextBox.Text ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(keyPath))
                {
                    _sshAgentService.RememberKeyPath(keyPath);
                }

                var result = await _sshAgentService.TestHostAsync(_host, keyPath);
                DetailsText.Text = string.IsNullOrWhiteSpace(result.Details) ? result.Message : result.Details;
                if (result.IsReady)
                {
                    TestResultText.Text = result.Message;
                    TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Success");
                    DialogResult = true;
                    Close();
                    return;
                }

                TestResultText.Text = result.Message;
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
            }
            catch (Exception ex)
            {
                TestResultText.Text = ex.Message;
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SetBusy(bool busy)
        {
            TestButton.IsEnabled = !busy;
            TestButton.Content = busy ? "Working..." : "Test connection";
        }

        private static string HostKeysButtonLabel(string host)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            if (h.Contains("gitlab"))
            {
                return "Open GitLab SSH keys";
            }

            if (h.Contains("bitbucket") || h.Contains("atlassian"))
            {
                return "Open Bitbucket SSH keys";
            }

            return "Open GitHub SSH keys";
        }

        private static string BuildDetails(GitSshStatus status)
        {
            var ssh = string.IsNullOrWhiteSpace(status.SshExe) ? "ssh.exe not found" : status.SshExe;
            var keys = status.DiscoveredKeys.Count == 0
                ? "none"
                : string.Join(", ", status.DiscoveredKeys);
            return $"Host: {status.Host}{Environment.NewLine}ssh: {ssh}{Environment.NewLine}Keys: {keys}";
        }
    }
}
