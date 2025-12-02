using System;
using System.Collections.Generic;
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

        public ConnectionProfile? SelectedProfile { get; private set; }

        public ConnectionManagerWindow()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            
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
            _currentProfile.Name = NameBox.Text;
            _currentProfile.Host = HostBox.Text;
            _currentProfile.Username = UserBox.Text;
            _currentProfile.Password = EncryptionService.Encrypt(PassBox.Password);
            _currentProfile.UseSSH = SftpRadio.IsChecked == true;
            
            if (int.TryParse(PortBox.Text, out int port)) _currentProfile.Port = port;

            // Advanced Fields
            _currentProfile.RemotePath = RootPathBox.Text;
            _currentProfile.WebServerUrl = WebUrlBox.Text;
            _currentProfile.PassiveMode = PassiveModeCheck.IsChecked == true;
            _currentProfile.ShowHiddenFiles = ShowHiddenCheck.IsChecked == true;
            if (int.TryParse(KeepAliveBox.Text, out int keepAlive)) _currentProfile.KeepAliveSeconds = keepAlive;

            // Database Fields
            if (DbTypeCombo.SelectedItem is DatabaseType dbType) _currentProfile.DbType = dbType;
            _currentProfile.DbHost = DbHostBox.Text;
            if (int.TryParse(DbPortBox.Text, out int dbPort)) _currentProfile.DbPort = dbPort;
            _currentProfile.DbUsername = DbUserBox.Text;
            _currentProfile.DbPassword = EncryptionService.Encrypt(DbPassBox.Password);
            _currentProfile.DbName = DbNameBox.Text;

            // Save to disk
            _configService.AddOrUpdateConnection(_currentProfile);
            
            SelectedProfile = _currentProfile;
            DialogResult = true;
            Close();
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

        private void TestDatabase_Click(object sender, RoutedEventArgs e)
        {
            ModernMessageBox.Show("Database testing is temporarily disabled to fix crashes.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}