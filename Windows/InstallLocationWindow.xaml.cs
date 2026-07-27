using System;
using System.IO;
using System.Windows;
using GitDeployPro.Services.Update;
using Forms = System.Windows.Forms;

namespace GitDeployPro.Windows
{
    public enum InstallLocationChoice
    {
        Cancel,
        Install,
        RunPortable
    }

    public partial class InstallLocationWindow : Window
    {
        public InstallLocationChoice Choice { get; private set; } = InstallLocationChoice.Cancel;

        /// <summary>Final product folder, e.g. D:\Apps\GitDeployPro</summary>
        public string SelectedInstallDirectory { get; private set; } = AppInstallPaths.DefaultInstallDirectory;

        public InstallLocationWindow()
        {
            InitializeComponent();
            SelectedInstallDirectory = AppInstallPaths.DefaultInstallDirectory;
            PathTextBox.Text = SelectedInstallDirectory;
            RefreshFreeSpace();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select a parent folder for GitDeploy Pro",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            try
            {
                var initial = Path.GetDirectoryName(SelectedInstallDirectory);
                if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                {
                    dialog.SelectedPath = initial;
                }
            }
            catch
            {
            }

            if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            SelectedInstallDirectory = AppInstallPaths.ResolveProductInstallDirectory(dialog.SelectedPath);
            PathTextBox.Text = SelectedInstallDirectory;
            RefreshFreeSpace();
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            try
            {
                var dir = AppInstallPaths.ResolveProductInstallDirectory(PathTextBox.Text);
                SelectedInstallDirectory = dir;

                if (!AppInstallPaths.TryGetDriveFreeBytes(dir, out var free) || free < AppInstallPaths.MinimumFreeBytes)
                {
                    StatusText.Text =
                        $"Not enough free space (need about {AppInstallPaths.FormatBytes(AppInstallPaths.MinimumFreeBytes)}). " +
                        "Choose another drive.";
                    RefreshFreeSpace();
                    return;
                }

                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                Choice = InstallLocationChoice.Install;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Cannot install here: {ex.Message}";
            }
        }

        private void PortableButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = InstallLocationChoice.RunPortable;
            DialogResult = false;
            Close();
        }

        private void RefreshFreeSpace()
        {
            try
            {
                var dir = SelectedInstallDirectory;
                if (AppInstallPaths.TryGetDriveFreeBytes(dir, out var free))
                {
                    var ok = free >= AppInstallPaths.MinimumFreeBytes;
                    FreeSpaceText.Text =
                        $"Free space on this drive: {AppInstallPaths.FormatBytes(free)}" +
                        (ok ? string.Empty : $" (need ~{AppInstallPaths.FormatBytes(AppInstallPaths.MinimumFreeBytes)})");
                    FreeSpaceText.Foreground = ok
                        ? (TryFindResource("Text.Muted") as System.Windows.Media.Brush
                           ?? System.Windows.Media.Brushes.Gray)
                        : (TryFindResource("Status.Warning") as System.Windows.Media.Brush
                           ?? System.Windows.Media.Brushes.Orange);
                    InstallButton.IsEnabled = ok;
                }
                else
                {
                    FreeSpaceText.Text = "Free space: unknown";
                    InstallButton.IsEnabled = true;
                }
            }
            catch
            {
                FreeSpaceText.Text = "Free space: unknown";
                InstallButton.IsEnabled = true;
            }
        }
    }
}
