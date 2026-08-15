using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Remote;

namespace GitDeployPro.Windows
{
    public partial class RemoteBrowserWindow : Window
    {
        public string SelectedPath { get; private set; } = "/";

        private readonly ConnectionProfile _profile;
        private IRemoteFileService? _service;

        public RemoteBrowserWindow(ConnectionProfile profile)
        {
            InitializeComponent();
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Loaded += RemoteBrowserWindow_Loaded;
            Closed += RemoteBrowserWindow_Closed;
        }

        private async void RemoteBrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await ConnectAndList("/");
        }

        private void RemoteBrowserWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                _service?.Abort();
            }
            catch
            {
                // Ignore abort during close.
            }

            _service = null;
        }

        private async Task ConnectAndList(string path)
        {
            try
            {
                if (_service == null || !_service.IsConnected)
                {
                    _service = _profile.UseSSH
                        ? new SftpRemoteFileService()
                        : new FtpRemoteFileService();
                    await _service.ConnectAsync(_profile);
                }

                var normalized = string.IsNullOrWhiteSpace(path)
                    ? "/"
                    : RemotePathResolver.NormalizeRemoteBase(path);
                var items = await _service.ListDirectoryAsync(normalized);
                var list = new List<RemoteItem>();

                if (!string.Equals(normalized.TrimEnd('/'), string.Empty, StringComparison.Ordinal)
                    && normalized != "/")
                {
                    var parent = RemotePathResolver.GetParentDirectory(normalized, "/");
                    list.Add(new RemoteItem { Name = "..", Icon = "⬆️", IsDirectory = true, Path = parent });
                }

                foreach (var item in items
                    .Where(entry => entry.IsDirectory
                        && !string.Equals(entry.Name, ".", StringComparison.Ordinal)
                        && !string.Equals(entry.Name, "..", StringComparison.Ordinal))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(new RemoteItem
                    {
                        Name = item.Name,
                        Icon = "📁",
                        IsDirectory = true,
                        Path = string.IsNullOrWhiteSpace(item.FullPath) ? item.Name : item.FullPath
                    });
                }

                FileListBox.ItemsSource = list;
                PathTextBox.Text = normalized;
                SelectedPath = normalized;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error listing files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileListBox.SelectedItem is RemoteItem item && item.IsDirectory)
            {
                await ConnectAndList(item.Path);
            }
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public class RemoteItem
        {
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "";
            public string Path { get; set; } = "";
            public bool IsDirectory { get; set; }
        }
    }
}
