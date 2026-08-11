using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
        private string _connectionSearchText = string.Empty;
        private readonly ObservableCollection<PathMapping> _pathMappings = new ObservableCollection<PathMapping>();
        private readonly NavicatImportService _navicatImportService = new NavicatImportService();
        private CancellationTokenSource? _remoteTestCts;
        private bool _isTestingRemote;
        private CancellationTokenSource? _databaseTestCts;
        private bool _isTestingDatabase;
        private bool _isSyncingPasswordEditors;
        private bool _isRemotePasswordVisible;
        private bool _isDatabasePasswordVisible;
        private string? _preferredProjectProfileId;

        public ConnectionProfile? SelectedProfile { get; private set; }

        /// <summary>Project-assigned connection profile id — pinned to top of the list when set.</summary>
        public string? PreferredProjectProfileId
        {
            get => _preferredProjectProfileId;
            set => _preferredProjectProfileId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public ConnectionManagerWindow(string? preferredProjectProfileId = null)
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _preferredProjectProfileId = string.IsNullOrWhiteSpace(preferredProjectProfileId)
                ? ResolveCurrentProjectConnectionProfileId()
                : preferredProjectProfileId.Trim();
            PathMappingsList.ItemsSource = _pathMappings;
            
            try
            {
                // Safe Enum Binding
                DbTypeCombo.ItemsSource = Enum.GetValues(typeof(DatabaseType));
                
                LoadProfiles();
                ResetPasswordEditorState();
                UpdateEditPanelVisibility();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading Connection Manager: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? ResolveCurrentProjectConnectionProfileId()
        {
            try
            {
                var lastProjectPath = _configService.LoadGlobalConfig().LastProjectPath;
                if (string.IsNullOrWhiteSpace(lastProjectPath))
                {
                    return null;
                }

                return _configService.LoadProjectConfig(lastProjectPath).ConnectionProfileId;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<ConnectionProfile> OrderProfilesForDisplay(IEnumerable<ConnectionProfile> profiles)
        {
            var list = profiles.ToList();
            if (string.IsNullOrWhiteSpace(_preferredProjectProfileId))
            {
                return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
            }

            return list
                .OrderByDescending(p => string.Equals(p.Id, _preferredProjectProfileId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        }
        private void ImportNavicatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Navicat Connections",
                Filter = "Navicat Connections (*.ncx;*.xml)|*.ncx;*.xml|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                var result = _navicatImportService.Import(dialog.FileName);
                if (result.Profiles.Count == 0)
                {
                    ModernMessageBox.Show("No supported Navicat connections were found in that file.", "Navicat Import", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var existingNames = new HashSet<string>(_profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                int imported = 0;
                foreach (var profile in result.Profiles)
                {
                    profile.Name = EnsureUniqueName(profile.Name, existingNames);
                    _configService.AddOrUpdateConnection(profile);
                    existingNames.Add(profile.Name);
                    imported++;
                }

                LoadProfiles();
                var message = new StringBuilder();
                message.AppendLine($"Imported {imported} connection(s) from Navicat.");
                if (result.Warnings.Count > 0)
                {
                    message.AppendLine().AppendLine("Warnings:");
                    foreach (var warning in result.Warnings.Take(5))
                    {
                        message.AppendLine($"• {warning}");
                    }

                    if (result.Warnings.Count > 5)
                    {
                        message.AppendLine("• ...");
                    }
                }

                ModernMessageBox.Show(message.ToString(), "Navicat Import", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to import Navicat connections: {ex.Message}", "Navicat Import", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EnsureUniqueName(string baseName, HashSet<string> existingNames)
        {
            if (!existingNames.Contains(baseName))
            {
                return baseName;
            }

            int counter = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} ({counter++})";
            } while (existingNames.Contains(candidate));

            return candidate;
        }


        private void LoadProfiles()
        {
            try 
            {
                _profiles = _configService.LoadConnections();
                RefreshConnectionsList(
                    preferredProfileId: _preferredProjectProfileId,
                    selectFirstWhenMissing: false);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading profiles: {ex.Message}", "Data Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _profiles = new List<ConnectionProfile>();
                RefreshConnectionsList(
                    preferredProfileId: _preferredProjectProfileId,
                    selectFirstWhenMissing: false);
            }
        }

        private IEnumerable<ConnectionProfile> GetFilteredProfiles()
        {
            if (string.IsNullOrWhiteSpace(_connectionSearchText))
            {
                return _profiles;
            }

            var query = _connectionSearchText.Trim();
            return _profiles.Where(profile =>
                (!string.IsNullOrWhiteSpace(profile.Name) && profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(profile.Host) && profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(profile.DbName) && profile.DbName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                profile.DbType.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ((profile.UseSSH ? "sftp ssh" : "ftp").Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        private void RefreshConnectionsList(string? preferredProfileId = null, bool selectFirstWhenMissing = true)
        {
            var keepSelectionId = preferredProfileId
                                  ?? (ConnectionsList.SelectedItem as ConnectionProfile)?.Id
                                  ?? _currentProfile?.Id;

            var filtered = OrderProfilesForDisplay(GetFilteredProfiles()).ToList();

            ConnectionsList.ItemsSource = null;
            ConnectionsList.ItemsSource = filtered;

            if (filtered.Count == 0)
            {
                ConnectionsList.SelectedItem = null;
                _currentProfile = null;
                EditPanel.IsEnabled = false;
                _pathMappings.Clear();
                UpdateEditPanelVisibility();
                return;
            }

            var selected = !string.IsNullOrWhiteSpace(keepSelectionId)
                ? filtered.FirstOrDefault(p => string.Equals(p.Id, keepSelectionId, StringComparison.OrdinalIgnoreCase))
                : null;

            if (selected != null)
            {
                ConnectionsList.SelectedItem = selected;
                ConnectionsList.ScrollIntoView(selected);
            }
            else if (selectFirstWhenMissing)
            {
                ConnectionsList.SelectedIndex = 0;
                if (ConnectionsList.SelectedItem != null)
                {
                    ConnectionsList.ScrollIntoView(ConnectionsList.SelectedItem);
                }
            }
            else
            {
                ConnectionsList.SelectedItem = null;
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
            SshStartupCommandBox.Text = profile.SshStartupCommand ?? string.Empty;
            RunSshStartupCommandCheck.IsChecked = profile.RunSshStartupCommand;

            // Database Fields
            DbTypeCombo.SelectedItem = profile.DbType;
            DbHostBox.Text = string.IsNullOrEmpty(profile.DbHost) ? "127.0.0.1" : profile.DbHost;
            DbPortBox.Text = profile.DbPort <= 0 ? "3306" : profile.DbPort.ToString();
            DbUserBox.Text = profile.DbUsername;
            DbPassBox.Password = EncryptionService.Decrypt(profile.DbPassword);
            DbNameBox.Text = profile.DbName;
            ResetPasswordEditorState();

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
                GetRemotePassword(),
                int.Parse(PortBox.Text)
            );

            if (WindowOwnerService.ShowDialogOwned(browser, this) == true)
            {
                RootPathBox.Text = browser.SelectedPath;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var newProfile = new ConnectionProfile
            {
                Name = string.Empty,
                Host = "",
                Username = ""
            };

            _configService.AddOrUpdateConnection(newProfile);
            LoadProfiles();

            var selected = _profiles.FirstOrDefault(p => p.Id == newProfile.Id);
            if (selected == null)
            {
                return;
            }

            // Keep newly created profiles at the top for immediate editing.
            _profiles.Remove(selected);
            _profiles.Insert(0, selected);
            _configService.SaveConnections(_profiles);
            RefreshConnectionsList(selected.Id);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                NameBox.Focus();
                NameBox.CaretIndex = NameBox.Text?.Length ?? 0;
            }), System.Windows.Threading.DispatcherPriority.Background);
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
                    SshStartupCommand = profile.SshStartupCommand,
                    RunSshStartupCommand = profile.RunSshStartupCommand,
                    IsFavorite = profile.IsFavorite,
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
                RefreshConnectionsList(newProfile.Id);
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
            var connectionName = (NameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                ModernMessageBox.Show("Connection name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            _currentProfile.Name = connectionName;
            _currentProfile.Host = (HostBox.Text ?? string.Empty).Trim();
            _currentProfile.Username = (UserBox.Text ?? string.Empty).Trim();
            _currentProfile.Password = EncryptionService.Encrypt(GetRemotePassword());
            _currentProfile.UseSSH = SftpRadio.IsChecked == true;
            
            if (int.TryParse(PortBox.Text, out int port)) _currentProfile.Port = port;

            // Advanced Fields
            _currentProfile.RemotePath = (RootPathBox.Text ?? string.Empty).Trim();
            _currentProfile.WebServerUrl = (WebUrlBox.Text ?? string.Empty).Trim();
            _currentProfile.PassiveMode = PassiveModeCheck.IsChecked == true;
            _currentProfile.ShowHiddenFiles = ShowHiddenCheck.IsChecked == true;
            if (int.TryParse(KeepAliveBox.Text, out int keepAlive)) _currentProfile.KeepAliveSeconds = keepAlive;
            _currentProfile.SshStartupCommand = (SshStartupCommandBox.Text ?? string.Empty).Trim();
            _currentProfile.RunSshStartupCommand = RunSshStartupCommandCheck.IsChecked == true;
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
            _currentProfile.DbPassword = EncryptionService.Encrypt(GetDatabasePassword());
            _currentProfile.DbName = (DbNameBox.Text ?? string.Empty).Trim();

            // Save to disk
            _configService.AddOrUpdateConnection(_currentProfile);
            
            SelectedProfile = _currentProfile;
            DialogResult = true;
            Close();
        }

        private void FillStartupFromRootPathButton_Click(object sender, RoutedEventArgs e)
        {
            SshStartupCommandBox.Text = BuildCdStartupCommand(RootPathBox.Text);
        }

        private static string BuildCdStartupCommand(string? remotePath)
        {
            var path = (remotePath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(path))
            {
                return "cd";
            }

            var escaped = path.Replace("'", "'\\''", StringComparison.Ordinal);
            return $"cd '{escaped}'";
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
            if (_isTestingRemote)
            {
                return;
            }

            string host = (HostBox.Text ?? string.Empty).Trim();
            string user = (UserBox.Text ?? string.Empty).Trim();
            string pass = GetRemotePassword();
            int port = int.TryParse(PortBox.Text, out int p) ? p : 21;
            bool isSsh = SftpRadio.IsChecked == true;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
            {
                ModernMessageBox.Show("Host and username are required for connection test.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isTestingRemote = true;
            _remoteTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            SetRemoteTestUiState(true, isSsh ? "Testing SFTP connection..." : "Testing FTP connection...");

            try
            {
                if (isSsh)
                {
                    await TestSftpConnectionAsync(host, user, pass, port, _remoteTestCts.Token);
                }
                else
                {
                    await TestFtpConnectionAsync(host, user, pass, port, _remoteTestCts.Token);
                }

                SetRemoteTestUiState(false, "Connection successful.");
                ModernMessageBox.Show("Connection successful. ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetRemoteTestUiState(false, "Connection test cancelled.");
            }
            catch (Exception ex)
            {
                SetRemoteTestUiState(false, $"Connection failed: {ex.Message}");
                ModernMessageBox.Show($"Connection failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isTestingRemote = false;
                _remoteTestCts?.Dispose();
                _remoteTestCts = null;
                SetRemoteTestUiState(false, RemoteTestStatusText.Text);
            }
        }

        private async Task TestFtpConnectionAsync(string host, string user, string pass, int port, CancellationToken token)
        {
            using var client = new AsyncFtpClient(host, user, pass, port <= 0 ? 21 : port);
            client.Config.DataConnectionType = PassiveModeCheck.IsChecked == true
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive;
            client.Config.ReadTimeout = 15000;
            client.Config.DataConnectionReadTimeout = 15000;
            client.Config.SocketKeepAlive = true;
            client.Config.RetryAttempts = 1;

            await client.AutoConnect(token);
        }

        private async Task TestSftpConnectionAsync(string host, string user, string pass, int port, CancellationToken token)
        {
            var authMethods = new List<AuthenticationMethod>();
            string keyPath = _currentProfile?.PrivateKeyPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            {
                authMethods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyPath)));
            }

            if (!string.IsNullOrWhiteSpace(pass))
            {
                authMethods.Add(new PasswordAuthenticationMethod(user, pass));
            }

            if (authMethods.Count == 0)
            {
                throw new InvalidOperationException("Provide a password or private key for SFTP test.");
            }

            int sftpPort = port <= 0 ? 22 : (port == 21 ? 22 : port);
            var connectTask = Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(host, sftpPort, user, authMethods.ToArray());
                using var client = new SshClient(connectionInfo);
                client.Connect();
                client.Disconnect();
            }, token);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25), token);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask != connectTask)
            {
                throw new TimeoutException("SFTP test timed out.");
            }

            await connectTask;
        }

        private void SetRemoteTestUiState(bool isTesting, string statusMessage)
        {
            if (TestRemoteButton != null)
            {
                TestRemoteButton.IsEnabled = !isTesting;
                TestRemoteButton.Content = isTesting ? "Testing..." : "⚡ Test Connection";
            }

            if (RemoteTestOverlay != null)
            {
                RemoteTestOverlay.Visibility = isTesting ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RemoteTestOverlayStatusText != null)
            {
                RemoteTestOverlayStatusText.Text = statusMessage ?? string.Empty;
            }

            if (RemoteTestOverlayCancelButton != null)
            {
                RemoteTestOverlayCancelButton.IsEnabled = isTesting;
            }

            if (RemoteTestStatusText != null)
            {
                RemoteTestStatusText.Text = statusMessage ?? string.Empty;
            }
        }

        private void CancelRemoteTestButton_Click(object sender, RoutedEventArgs e)
        {
            _remoteTestCts?.Cancel();
            if (_isTestingRemote)
            {
                SetRemoteTestUiState(true, "Cancelling connection test...");
            }
        }

        private void SetDatabaseTestUiState(bool isTesting, string statusMessage)
        {
            if (TestDatabaseButton != null)
            {
                TestDatabaseButton.IsEnabled = !isTesting;
                TestDatabaseButton.Content = isTesting ? "Testing..." : "🔌 Test Database Connection";
            }

            if (DatabaseTestOverlay != null)
            {
                DatabaseTestOverlay.Visibility = isTesting ? Visibility.Visible : Visibility.Collapsed;
            }

            if (DatabaseTestOverlayStatusText != null)
            {
                DatabaseTestOverlayStatusText.Text = statusMessage ?? string.Empty;
            }

            if (DatabaseTestOverlayCancelButton != null)
            {
                DatabaseTestOverlayCancelButton.IsEnabled = isTesting;
            }

            if (DatabaseTestStatusText != null)
            {
                DatabaseTestStatusText.Text = statusMessage ?? string.Empty;
            }
        }

        private void CancelDatabaseTestButton_Click(object sender, RoutedEventArgs e)
        {
            _databaseTestCts?.Cancel();
            if (_isTestingDatabase)
            {
                SetDatabaseTestUiState(true, "Cancelling database test...");
            }
        }

        private async Task TestDatabaseConnectionAsync(DatabaseConnectionEntry entry, CancellationToken cancellationToken)
        {
            var connectTask = Task.Run(async () =>
            {
                await using var client = new DatabaseClient();
                await client.ConnectAsync(entry.ToConnectionInfo());
                await client.DisconnectAsync();
            }, cancellationToken);

            var delayTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completed = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(true);
            if (completed != connectTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Database test timed out.");
            }

            await connectTask.ConfigureAwait(true);
        }

        private async void TestDatabase_Click(object sender, RoutedEventArgs e)
        {
            if (_isTestingDatabase)
            {
                return;
            }

            if (DbTypeCombo.SelectedItem is not DatabaseType selectedType || selectedType == DatabaseType.None)
            {
                ModernMessageBox.Show("Please select a database type first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                DbTypeCombo.Focus();
                return;
            }

            var dbType = selectedType;

            if (dbType != DatabaseType.MySQL && dbType != DatabaseType.MariaDB)
            {
                ModernMessageBox.Show("Database testing currently supports MySQL / MariaDB only.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var host = string.IsNullOrWhiteSpace(DbHostBox.Text) ? "127.0.0.1" : DbHostBox.Text.Trim();
            var username = string.IsNullOrWhiteSpace(DbUserBox.Text) ? "root" : DbUserBox.Text.Trim();
            var password = GetDatabasePassword();
            var database = DbNameBox.Text?.Trim() ?? string.Empty;
            var connectionName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Database Test" : NameBox.Text.Trim();
            var useSshTunnel = SftpRadio.IsChecked == true;
            var sshHost = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
            var sshUsername = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim();
            var sshPassword = GetRemotePassword();
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
            _isTestingDatabase = true;
            _databaseTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            SetDatabaseTestUiState(true, "Testing database login and route...");

            try
            {
                await TestDatabaseConnectionAsync(entry, _databaseTestCts.Token);
                SetDatabaseTestUiState(false, "Database connection successful.");
                ModernMessageBox.Show("Database connection successful. ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetDatabaseTestUiState(false, "Database test cancelled.");
            }
            catch (Exception ex)
            {
                SetDatabaseTestUiState(false, $"Database test failed: {ex.Message}");
                ModernMessageBox.Show($"Database test failed: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isTestingDatabase = false;
                _databaseTestCts?.Dispose();
                _databaseTestCts = null;
                SetDatabaseTestUiState(false, DatabaseTestStatusText.Text);
            }
        }

        private void ConnectionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _connectionSearchText = ConnectionSearchBox.Text ?? string.Empty;
            RefreshConnectionsList();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectionSearchBox.Text = string.Empty;
            ConnectionSearchBox.Focus();
        }

        private static string NormalizePassword(string? value)
        {
            return (value ?? string.Empty).Trim();
        }

        private string GetRemotePassword()
        {
            return NormalizePassword(_isRemotePasswordVisible ? PassRevealTextBox.Text : PassBox.Password);
        }

        private string GetDatabasePassword()
        {
            return NormalizePassword(_isDatabasePasswordVisible ? DbPassRevealTextBox.Text : DbPassBox.Password);
        }

        private void ResetPasswordEditorState()
        {
            _isSyncingPasswordEditors = true;
            try
            {
                _isRemotePasswordVisible = false;
                _isDatabasePasswordVisible = false;

                PassBox.Visibility = Visibility.Visible;
                PassRevealTextBox.Visibility = Visibility.Collapsed;
                PassRevealTextBox.Text = PassBox.Password;
                PassRevealButton.Content = "👁";
                PassRevealButton.ToolTip = "Show password";

                DbPassBox.Visibility = Visibility.Visible;
                DbPassRevealTextBox.Visibility = Visibility.Collapsed;
                DbPassRevealTextBox.Text = DbPassBox.Password;
                DbPassRevealButton.Content = "👁";
                DbPassRevealButton.ToolTip = "Show password";
            }
            finally
            {
                _isSyncingPasswordEditors = false;
            }
        }

        private void TogglePasswordVisibility(PasswordBox passwordBox, System.Windows.Controls.TextBox revealTextBox, System.Windows.Controls.Button revealButton, ref bool isVisible)
        {
            _isSyncingPasswordEditors = true;
            try
            {
                isVisible = !isVisible;
                if (isVisible)
                {
                    revealTextBox.Text = passwordBox.Password;
                    passwordBox.Visibility = Visibility.Collapsed;
                    revealTextBox.Visibility = Visibility.Visible;
                    revealButton.Content = "🙈";
                    revealButton.ToolTip = "Hide password";
                    revealTextBox.Focus();
                    revealTextBox.CaretIndex = revealTextBox.Text.Length;
                }
                else
                {
                    passwordBox.Password = revealTextBox.Text;
                    revealTextBox.Visibility = Visibility.Collapsed;
                    passwordBox.Visibility = Visibility.Visible;
                    revealButton.Content = "👁";
                    revealButton.ToolTip = "Show password";
                    passwordBox.Focus();
                }
            }
            finally
            {
                _isSyncingPasswordEditors = false;
            }
        }

        private void SyncRevealTextFromPassword(PasswordBox passwordBox, System.Windows.Controls.TextBox revealTextBox)
        {
            if (_isSyncingPasswordEditors || revealTextBox.Visibility != Visibility.Visible)
            {
                return;
            }

            _isSyncingPasswordEditors = true;
            try
            {
                revealTextBox.Text = passwordBox.Password;
                revealTextBox.CaretIndex = revealTextBox.Text.Length;
            }
            finally
            {
                _isSyncingPasswordEditors = false;
            }
        }

        private void SyncPasswordFromRevealText(PasswordBox passwordBox, System.Windows.Controls.TextBox revealTextBox)
        {
            if (_isSyncingPasswordEditors)
            {
                return;
            }

            _isSyncingPasswordEditors = true;
            try
            {
                passwordBox.Password = revealTextBox.Text;
            }
            finally
            {
                _isSyncingPasswordEditors = false;
            }
        }

        private void PassRevealButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePasswordVisibility(PassBox, PassRevealTextBox, PassRevealButton, ref _isRemotePasswordVisible);
        }

        private void DbPassRevealButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePasswordVisibility(DbPassBox, DbPassRevealTextBox, DbPassRevealButton, ref _isDatabasePasswordVisible);
        }

        private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            SyncRevealTextFromPassword(PassBox, PassRevealTextBox);
        }

        private void DbPassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            SyncRevealTextFromPassword(DbPassBox, DbPassRevealTextBox);
        }

        private void PassRevealTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SyncPasswordFromRevealText(PassBox, PassRevealTextBox);
        }

        private void DbPassRevealTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SyncPasswordFromRevealText(DbPassBox, DbPassRevealTextBox);
        }
    }
}