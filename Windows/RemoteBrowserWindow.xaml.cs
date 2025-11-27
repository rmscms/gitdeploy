using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FluentFTP;
using GitDeployPro.Controls;

namespace GitDeployPro.Windows
{
    public partial class RemoteBrowserWindow : Window
    {
        public string SelectedPath { get; private set; } = "/";
        private AsyncFtpClient? _client;
        private string _host, _user, _pass;
        private int _port;

        public RemoteBrowserWindow(string host, string user, string pass, int port)
        {
            InitializeComponent();
            _host = host;
            _user = user;
            _pass = pass;
            _port = port;
            Loaded += RemoteBrowserWindow_Loaded;
        }

        private async void RemoteBrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await ConnectAndList("/");
        }

        private async Task ConnectAndList(string path)
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    _client = new AsyncFtpClient(_host, _user, _pass, _port);
                    await _client.Connect();
                }

                var items = await _client.GetListing(path);
                var list = new List<RemoteItem>();

                // Add "Up" folder if not root
                if (path != "/")
                {
                    var parent = System.IO.Path.GetDirectoryName(path)?.Replace("\\", "/") ?? "/";
                    list.Add(new RemoteItem { Name = "..", Icon = "⬆️", IsDirectory = true, Path = parent });
                }

                foreach (var item in items)
                {
                    if (item.Type == FtpObjectType.Directory)
                    {
                        list.Add(new RemoteItem { Name = item.Name, Icon = "📁", IsDirectory = true, Path = item.FullName });
                    }
                }

                FileListBox.ItemsSource = list;
                PathTextBox.Text = path;
                SelectedPath = path;
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
