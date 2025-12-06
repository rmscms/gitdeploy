using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services;
using GitDeployPro.Models;
using FluentFTP;
using Renci.SshNet;
using GitDeployPro.Controls; // For MessageBox
// using MySqlConnector; // Temporarily removed

using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class ConnectionManagerWindow : MetroWindow
    {
        private ConfigurationService _configService;
        private List<ConnectionProfile> _profiles = new List<ConnectionProfile>();
        private ConnectionProfile? _currentProfile;
        private readonly ObservableCollection<PathMapping> _pathMappings = new ObservableCollection<PathMapping>();

        public ConnectionProfile? SelectedProfile { get; private set; }

        public ConnectionManagerWindow()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            PathMappingsList.ItemsSource = _pathMappings;
            
            try
            {
                // Safe Enum Binding
                DbTypeCombo.ItemsSource = Enum.GetValues(typeof(DatabaseType));
                
                LoadProfiles();
                UpdateEditPanelVisibility();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading Connection Manager: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadProfiles()
        {
            try 
            {
                _profiles = _configService.LoadConnections();
                ConnectionsList.ItemsSource = null;
                ConnectionsList.ItemsSource = _profiles;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading profiles: {ex.Message}", "Data Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _profiles = new List<ConnectionProfile>();
            }
        }

        private void ConnectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConnectionsList.SelectedItem is ConnectionProfile profile)
            {
                _currentProfile = profile;
                PopulateForm(profile);
                EditPanel.IsEnabled = true;
            }
            else
            {
                EditPanel.IsEnabled = false;
                _pathMappings.Clear();
            }
            UpdateEditPanelVisibility();
        }

        private void UpdateEditPanelVisibility()
        {
            if (EmptyStateOverlay != null)
            {
                 EmptyStateOverlay.Visibility = EditPanel.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void PopulateForm(ConnectionProfile profile)
        {
            NameBox.Text = profile.Name;
            HostBox.Text = profile.Host;
            PortBox.Text = profile.Port.ToString();
            UserBox.Text = profile.Username;
            PassBox.Password = EncryptionService.Decrypt(profile.Password);
            
            if (profile.UseSSH) SftpRadio.IsChecked = true;
            else FtpRadio.IsChecked = true;

            // Advanced Fields
            RootPathBox.Text = profile.RemotePath;
            WebUrlBox.Text = profile.WebServerUrl;
            PassiveModeCheck.IsChecked = profile.PassiveMode;
            ShowHiddenCheck.IsChecked = profile.ShowHiddenFiles;
            KeepAliveBox.Text = profile.KeepAliveSeconds.ToString();

            // Database Fields
            DbTypeCombo.SelectedItem = profile.DbType;
            DbHostBox.Text = string.IsNullOrEmpty(profile.DbHost) ? "127.0.0.1" : profile.DbHost;
            DbPortBox.Text = profile.DbPort <= 0 ? "3306" : profile.DbPort.ToString();
            DbUserBox.Text = profile.DbUsername;
            DbPassBox.Password = EncryptionService.Decrypt(profile.DbPassword);
            DbNameBox.Text = profile.DbName;

            PopulatePathMappings(profile);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text))
            {
                ModernMessageBox.Show("Please enter a Host first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SftpRadio.IsChecked == true)
            {
                ModernMessageBox.Show("Directory browsing is currently only available for FTP connections.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            var browser = new RemoteBrowserWindow(
                HostBox.Text,
                UserBox.Text,
                PassBox.Password,
                int.Parse(PortBox.Text)
            );

            if (browser.ShowDialog() == true)
            {
                RootPathBox.Text = browser.SelectedPath;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var newProfile = new ConnectionProfile
            {
                Name = "New Connection",
                Host = "",
                Username = ""
            };
            _configService.AddOrUpdateConnection(newProfile);
            LoadProfiles();
            ConnectionsList.SelectedItem = newProfile; // Select it
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionsList.SelectedItem is ConnectionProfile profile)
            {
                if (ModernMessageBox.Show($"Delete '{profile.Name}'?", "Confirm", MessageBoxButton.YesNo) == true)
                {
                    _configService.DeleteConnection(profile.Id.ToString());
                    LoadProfiles();
                }
            }
        }

        // --- Context Menu Handlers (MUST BE PUBLIC for XAML to find them easily in some contexts) ---
        public void DuplicateConnection_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionsList.SelectedItem is ConnectionProfile profile)
            {
                var newProfile = new ConnectionProfile
                {
                    Name = profile.Name + " (Copy)",
                    Host = profile.Host,
                    Port = profile.Port,
                    Username = profile.Username,
                    Password = profile.Password,
                    UseSSH = profile.UseSSH,
                    PrivateKeyPath = profile.PrivateKeyPath,
                    RemotePath = profile.RemotePath,
                    WebServerUrl = profile.WebServerUrl,
                    PassiveMode = profile.PassiveMode,
                    ShowHiddenFiles = profile.ShowHiddenFiles,
                    KeepAliveSeconds = profile.KeepAliveSeconds,
                    PathMappings = profile.PathMappings?
                        .Select(m => new PathMapping { LocalPath = m.LocalPath, RemotePath = m.RemotePath })
                        .ToList() ?? new List<PathMapping>(),
                    DbType = profile.DbType,
                    DbHost = profile.DbHost,
                    DbPort = profile.DbPort,
                    DbUsername = profile.DbUsername,
                    DbPassword = profile.DbPassword,
                    DbName = profile.DbName
                };
                
                _configService.AddOrUpdateConnection(newProfile);
                LoadProfiles();
                
                 foreach(var p in _profiles) {
                    if(p.Id == newProfile.Id) {
                         ConnectionsList.SelectedItem = p;
                         break;
                    }
                 }
            }
        }

        public void DeleteConnection_Context_Click(object sender, RoutedEventArgs e)
        {
            DeleteButton_Click(sender, e);
        }
        // ------------------------------------------------------------------------------------------

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null) return;

            // Update object
            _currentProfile.Name = (NameBox.Text ?? string.Empty).Trim();
            _currentProfile.Host = (HostBox.Text ?? string.Empty).Trim();
            _currentProfile.Username = (UserBox.Text ?? string.Empty).Trim();
            _currentProfile.Password = EncryptionService.Encrypt(PassBox.Password);
            _currentProfile.UseSSH = SftpRadio.IsChecked == true;
            
            if (int.TryParse(PortBox.Text, out int port)) _currentProfile.Port = port;

            // Advanced Fields
            _currentProfile.RemotePath = (RootPathBox.Text ?? string.Empty).Trim();
            _currentProfile.WebServerUrl = (WebUrlBox.Text ?? string.Empty).Trim();
            _currentProfile.PassiveMode = PassiveModeCheck.IsChecked == true;
            _currentProfile.ShowHiddenFiles = ShowHiddenCheck.IsChecked == true;
            if (int.TryParse(KeepAliveBox.Text, out int keepAlive)) _currentProfile.KeepAliveSeconds = keepAlive;
            _currentProfile.PathMappings = _pathMappings
                .Select(pm => new PathMapping
                {
                    LocalPath = pm.LocalPath ?? string.Empty,
                    RemotePath = pm.RemotePath ?? string.Empty
                })
                .ToList();

            // Database Fields
            if (DbTypeCombo.SelectedItem is DatabaseType dbType) _currentProfile.DbType = dbType;
            _currentProfile.DbHost = (DbHostBox.Text ?? string.Empty).Trim();
            if (int.TryParse(DbPortBox.Text, out int dbPort)) _currentProfile.DbPort = dbPort;
            _currentProfile.DbUsername = (DbUserBox.Text ?? string.Empty).Trim();
            _currentProfile.DbPassword = EncryptionService.Encrypt(DbPassBox.Password);
            _currentProfile.DbName = (DbNameBox.Text ?? string.Empty).Trim();

            // Save to disk
            _configService.AddOrUpdateConnection(_currentProfile);
            
            SelectedProfile = _currentProfile;
            DialogResult = true;
            Close();
        }

        private void PopulatePathMappings(ConnectionProfile profile)
        {
            _pathMappings.Clear();
            if (profile.PathMappings != null)
            {
                foreach (var mapping in profile.PathMappings)
                {
                    if (mapping == null) continue;
                    _pathMappings.Add(new PathMapping
                    {
                        LocalPath = mapping.LocalPath ?? string.Empty,
                        RemotePath = mapping.RemotePath ?? string.Empty
                    });
                }
            }
            LocalMappingTextBox.Text = string.Empty;
            RemoteMappingTextBox.Text = string.Empty;
            PathMappingsList.SelectedItem = null;
        }

        private void PathMappingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PathMappingsList.SelectedItem is PathMapping mapping)
            {
                LocalMappingTextBox.Text = mapping.LocalPath;
                RemoteMappingTextBox.Text = mapping.RemotePath;
            }
        }

        private void SaveMappingButton_Click(object sender, RoutedEventArgs e)
        {
            var local = NormalizeLocalPath(LocalMappingTextBox.Text);
            var remote = NormalizeRemotePath(RemoteMappingTextBox.Text);

            if (PathMappingsList.SelectedItem is PathMapping selected)
            {
                selected.LocalPath = local;
                selected.RemotePath = remote;
                PathMappingsList.Items.Refresh();
            }
            else
            {
                var existing = _pathMappings.FirstOrDefault(pm => pm.LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.RemotePath = remote;
                    PathMappingsList.Items.Refresh();
                }
                else
                {
                    _pathMappings.Add(new PathMapping { LocalPath = local, RemotePath = remote });
                }
            }

            LocalMappingTextBox.Text = string.Empty;
            RemoteMappingTextBox.Text = string.Empty;
            PathMappingsList.SelectedItem = null;
        }

        private void DeleteMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (PathMappingsList.SelectedItem is PathMapping mapping)
            {
                _pathMappings.Remove(mapping);
                PathMappingsList.SelectedItem = null;
                LocalMappingTextBox.Text = string.Empty;
                RemoteMappingTextBox.Text = string.Empty;
            }
        }

        private string NormalizeLocalPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var trimmed = input.Trim();
            if (trimmed == ".") trimmed = string.Empty;
            trimmed = trimmed.Trim().TrimStart('\\', '/').Trim();
            trimmed = trimmed.Replace("\\", "/");
            return trimmed;
        }

        private string NormalizeRemotePath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "/";
            var trimmed = input.Trim();
            trimmed = trimmed.Replace("\\", "/");
            if (!trimmed.StartsWith("/")) trimmed = "/" + trimmed;
            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0) trimmed = "/";
            return trimmed;
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            string host = HostBox.Text;
            string user = UserBox.Text;
            string pass = PassBox.Password;
            int port = int.TryParse(PortBox.Text, out int p) ? p : 21;
            bool isSsh = SftpRadio.IsChecked == true;

            try
            {
                if (isSsh)
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var connectionInfo = new ConnectionInfo(host, port == 21 ? 22 : port, user,
                            new PasswordAuthenticationMethod(user, pass));
                        using (var client = new SshClient(connectionInfo))
                        {
                            client.Connect();
                            client.Disconnect();
                        }
                    });
                }
                else
                {
                    using (var client = new AsyncFtpClient(host, user, pass, port))
                    {
                        await client.AutoConnect();
                        await client.Disconnect();
                    }
                }
                ModernMessageBox.Show("Connection Successful! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Connection Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void TestDatabase_Click(object sender, RoutedEventArgs e)
        {
            var dbType = DbTypeCombo.SelectedItem is DatabaseType selected && selected != DatabaseType.None
                ? selected
                : DatabaseType.MySQL;

            if (dbType != DatabaseType.MySQL && dbType != DatabaseType.MariaDB)
            {
                ModernMessageBox.Show("Database testing currently supports MySQL / MariaDB only.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var host = string.IsNullOrWhiteSpace(DbHostBox.Text) ? "127.0.0.1" : DbHostBox.Text.Trim();
            var username = string.IsNullOrWhiteSpace(DbUserBox.Text) ? "root" : DbUserBox.Text.Trim();
            var password = DbPassBox.Password ?? string.Empty;
            var database = DbNameBox.Text?.Trim() ?? string.Empty;
            var connectionName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Database Test" : NameBox.Text.Trim();
            var button = TestDatabaseButton;
            var originalContent = button.Content;
            var useSshTunnel = SftpRadio.IsChecked == true;
            var sshHost = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
            var sshUsername = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim();
            var sshPassword = PassBox.Password ?? string.Empty;
            var sshPortText = string.IsNullOrWhiteSpace(PortBox.Text) ? string.Empty : PortBox.Text.Trim();
            var sshPort = int.TryParse(sshPortText, out var parsedSshPort) ? parsedSshPort : (useSshTunnel ? 22 : 21);
            if (sshPort <= 0)
            {
                sshPort = useSshTunnel ? 22 : 21;
            }
            var privateKeyPath = _currentProfile?.PrivateKeyPath ?? string.Empty;

            if (!int.TryParse(DbPortBox.Text, out int port) || port <= 0)
            {
                port = 3306;
            }

            var entry = new DatabaseConnectionEntry
            {
                Name = connectionName,
                DbType = dbType,
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                DatabaseName = database,
                UseSshTunnel = useSshTunnel,
                SshHost = sshHost,
                SshPort = useSshTunnel ? sshPort : 0,
                SshUsername = sshUsername,
                SshPassword = sshPassword,
                SshPrivateKeyPath = privateKeyPath
            };

            button.IsEnabled = false;
            button.Content = "Testing...";

            try
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(entry.ToConnectionInfo());
                await client.DisconnectAsync();

                ModernMessageBox.Show("Database connection successful. ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Database test failed: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = originalContent;
            }
        }
    }
}