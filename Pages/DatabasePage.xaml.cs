using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using Button = System.Windows.Controls.Button;

namespace GitDeployPro.Pages
{
    public partial class DatabasePage : Page
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<DatabaseConnectionEntry> _connections = new();
        private readonly ObservableCollection<string> _databaseOptions = new();
        private readonly ObservableCollection<string> _tables = new();
        private readonly List<string> _tableCache = new();
        private readonly DatabaseClient _client = new();

        private DatabaseConnectionEntry? _activeConnection;
        private bool _isInitialized;
        private bool _sidebarAdjusted;
        private bool _collapsedSidebarByPage;

        public DatabasePage()
        {
            InitializeComponent();
            Loaded += DatabasePage_Loaded;
            Unloaded += DatabasePage_Unloaded;

            ConnectionsList.ItemsSource = _connections;
            DatabaseSelector.ItemsSource = _databaseOptions;
            TablesList.ItemsSource = _tables;
        }

        private void DatabasePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_sidebarAdjusted)
            {
                UpdateSidebarState(true);
                _sidebarAdjusted = true;
            }
            if (_isInitialized) return;
            _isInitialized = true;
            LoadSavedConnections();
        }

        private async void DatabasePage_Unloaded(object sender, RoutedEventArgs e)
        {
            await _client.DisconnectAsync();
            UpdateSidebarState(false);
            _sidebarAdjusted = false;
        }

        private void LoadSavedConnections()
        {
            _connections.Clear();

            var localEntry = DatabaseConnectionEntry.CreateLocalDefault();
            _connections.Add(localEntry);

            var profiles = _configService.LoadConnections();
            foreach (var profile in profiles)
            {
                if (profile.DbType == DatabaseType.None) continue;
                _connections.Add(DatabaseConnectionEntry.FromProfile(profile));
            }

            var hasProfiles = _connections.Any(c => c.IsFromProfile);
            ConnectionsEmptyState.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void QuickConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var host = string.IsNullOrWhiteSpace(QuickHostBox.Text) ? "127.0.0.1" : QuickHostBox.Text.Trim();
            var port = ParsePort(QuickPortBox.Text);
            var username = string.IsNullOrWhiteSpace(QuickUserBox.Text) ? "root" : QuickUserBox.Text.Trim();
            var databaseName = QuickDatabaseBox.Text?.Trim() ?? string.Empty;

            var entry = new DatabaseConnectionEntry
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Local Quick Connect",
                DbType = GetSelectedDbType(),
                Host = host,
                Port = port,
                Username = username,
                Password = QuickPasswordBox.Password,
                DatabaseName = databaseName,
                Description = $"{host}:{port} · {username}",
                IsLocal = true
            };

            await ConnectToEntryAsync(entry);
        }

        private async void ConnectProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is DatabaseConnectionEntry entry)
            {
                await ConnectToEntryAsync(entry);
            }
        }

        private async Task ConnectToEntryAsync(DatabaseConnectionEntry entry)
        {
            if (!entry.SupportsCurrentVersion)
            {
                ModernMessageBox.Show("This build currently supports MySQL / MariaDB only. PostgreSQL, SQL Server and others are coming soon.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                ToggleSidebar(false);
                SetBusy(true, $"Connecting to {entry.Name}...");

                await _client.ConnectAsync(entry.ToConnectionInfo());
                _activeConnection = entry;

                ConnectionStatusText.Text = $"Connected to {entry.Name}";
                ConnectionDetailsText.Text = $"{entry.DbType} · {entry.Host}:{entry.Port}";
                ConnectionBadge.Text = entry.Badge;
                RefreshSchemaButton.IsEnabled = true;
                DisconnectButton.IsEnabled = true;
                DisconnectedState.Visibility = Visibility.Collapsed;
                DatabaseContent.Visibility = Visibility.Visible;

                await LoadSchemaAsync(entry);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to connect: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ShowDisconnectedState();
            }
            finally
            {
                ToggleSidebar(true);
                SetBusy(false);
            }
        }

        private async Task LoadSchemaAsync(DatabaseConnectionEntry entry)
        {
            try
            {
                SetBusy(true, "Loading databases...");
                _databaseOptions.Clear();

                var databases = await _client.GetDatabasesAsync();
                foreach (var db in databases)
                {
                    _databaseOptions.Add(db);
                }

                string? targetDb = null;
                if (!string.IsNullOrWhiteSpace(entry.DatabaseName) && databases.Contains(entry.DatabaseName))
                {
                    targetDb = entry.DatabaseName;
                }
                else if (databases.Any())
                {
                    targetDb = databases.First();
                }

                if (!string.IsNullOrWhiteSpace(targetDb))
                {
                    DatabaseSelector.SelectedItem = targetDb;
                }
                else
                {
                    DatabaseSelector.SelectedItem = null;
                    ActiveDatabaseText.Text = "None";
                    _tables.Clear();
                    _tableCache.Clear();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load schema: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LoadTablesAsync(string database)
        {
            try
            {
                SetBusy(true, "Fetching tables...");
                var tables = await _client.GetTablesAsync(database);

                _tableCache.Clear();
                _tableCache.AddRange(tables);
                ApplyTableFilter();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load tables: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshSchemaButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConnection == null) return;
            await LoadSchemaAsync(_activeConnection);
        }

        private async void DatabaseSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DatabaseSelector.SelectedItem is string db && _activeConnection != null)
            {
                ActiveDatabaseText.Text = db;
                await LoadTablesAsync(db);
            }
            else
            {
                ActiveDatabaseText.Text = "None";
                _tables.Clear();
                _tableCache.Clear();
            }
        }

        private void TablesSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyTableFilter();
        }

        private void ApplyTableFilter()
        {
            var query = TablesSearchBox.Text?.Trim() ?? string.Empty;
            _tables.Clear();

            var filtered = string.IsNullOrWhiteSpace(query)
                ? _tableCache
                : _tableCache.Where(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));

            foreach (var table in filtered)
            {
                _tables.Add(table);
            }
        }

        private async void TablesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TablesList.SelectedItem is string table &&
                DatabaseSelector.SelectedItem is string db)
            {
                await LoadTablePreviewAsync(db, table);
            }
        }

        private async Task LoadTablePreviewAsync(string database, string table)
        {
            try
            {
                SetQueryRunning(true);
                var preview = await _client.GetTablePreviewAsync(database, table);

                ResultsGrid.ItemsSource = preview.Table?.DefaultView;
                TableTitleText.Text = $"Table Preview · {table}";
                ResultStatusText.Text = preview.Message;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load table: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetQueryRunning(false);
            }
        }

        private async void RunQueryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_client.IsConnected)
            {
                ModernMessageBox.Show("Connect to a database first.", "No Connection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sql = QueryEditor.Text?.Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                ModernMessageBox.Show("Please enter a SQL query.", "Empty Query", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var database = DatabaseSelector.SelectedItem as string;
            try
            {
                SetQueryRunning(true);
                var result = await _client.ExecuteQueryAsync(sql, database);

                if (result.HasResultSet && result.Table != null)
                {
                    ResultsGrid.ItemsSource = result.Table.DefaultView;
                    TableTitleText.Text = "Query Result";
                }
                else
                {
                    ResultsGrid.ItemsSource = null;
                }

                ResultStatusText.Text = result.Message;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Query failed: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetQueryRunning(false);
            }
        }

        private void ClearQueryButton_Click(object sender, RoutedEventArgs e)
        {
            QueryEditor.Clear();
            ResultStatusText.Text = "Query editor cleared.";
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await _client.DisconnectAsync();
            ShowDisconnectedState();
        }

        private void ShowDisconnectedState()
        {
            _activeConnection = null;
            RefreshSchemaButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            ConnectionStatusText.Text = "No database connected";
            ConnectionDetailsText.Text = "Select or create a connection to get started.";
            ConnectionBadge.Text = "🛢️";
            DatabaseContent.Visibility = Visibility.Collapsed;
            DisconnectedState.Visibility = Visibility.Visible;
            ResultsGrid.ItemsSource = null;
            _databaseOptions.Clear();
            _tables.Clear();
            _tableCache.Clear();
            ActiveDatabaseText.Text = "None";
        }

        private void ToggleSidebar(bool enabled)
        {
            QuickConnectButton.IsEnabled = enabled;
            ManageConnectionsButton.IsEnabled = enabled;
            ConnectionsList.IsEnabled = enabled;
        }

        private void SetBusy(bool isBusy, string? message = null)
        {
            LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message))
            {
                LoadingText.Text = message;
            }
        }

        private void SetQueryRunning(bool isRunning)
        {
            QueryProgress.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            RunQueryButton.IsEnabled = !isRunning;
            ClearQueryButton.IsEnabled = !isRunning;
            TablesList.IsEnabled = !isRunning;
        }

        private int ParsePort(string? text)
        {
            return int.TryParse(text, out int port) && port > 0 ? port : 3306;
        }

        private DatabaseType GetSelectedDbType()
        {
            if (QuickDbTypeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag.Equals("MariaDB", StringComparison.OrdinalIgnoreCase)
                    ? DatabaseType.MariaDB
                    : DatabaseType.MySQL;
            }
            return DatabaseType.MySQL;
        }

        private async void OpenConnectionManager_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ConnectionManagerWindow();
            if (manager.ShowDialog() == true)
            {
                LoadSavedConnections();
            }
        }

        private void UpdateSidebarState(bool collapse)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                if (collapse)
                {
                    if (!mainWindow.IsSidebarCollapsed)
                    {
                        mainWindow.SetSidebarCollapsed(true);
                        _collapsedSidebarByPage = true;
                    }
                }
                else if (_collapsedSidebarByPage)
                {
                    mainWindow.SetSidebarCollapsed(false);
                    _collapsedSidebarByPage = false;
                }
            }

            if (ShowSidebarButton != null)
            {
                ShowSidebarButton.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ShowSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSidebarState(false);
            _sidebarAdjusted = false;
        }

        private class DatabaseConnectionEntry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public DatabaseType DbType { get; set; } = DatabaseType.MySQL;
            public string Host { get; set; } = "127.0.0.1";
            public int Port { get; set; } = 3306;
            public string Username { get; set; } = "root";
            public string Password { get; set; } = string.Empty;
            public string DatabaseName { get; set; } = string.Empty;
            public bool IsLocal { get; set; }
            public bool IsFromProfile { get; set; }

            public string Badge => DbType switch
            {
                DatabaseType.MariaDB => "🛢️",
                DatabaseType.MySQL => "🛢️",
                DatabaseType.PostgreSQL => "🐘",
                DatabaseType.SQLServer => "🗄️",
                DatabaseType.MongoDB => "🍃",
                DatabaseType.Redis => "🧠",
                _ => "🛢️"
            };

            public bool SupportsCurrentVersion => DbType == DatabaseType.MySQL || DbType == DatabaseType.MariaDB;

            public DatabaseConnectionInfo ToConnectionInfo()
            {
                return new DatabaseConnectionInfo
                {
                    Name = Name,
                    DbType = DbType,
                    Host = Host,
                    Port = Port,
                    Username = Username,
                    Password = Password,
                    DatabaseName = DatabaseName,
                    IsLocal = IsLocal,
                    SourceId = Id
                };
            }

            public static DatabaseConnectionEntry CreateLocalDefault()
            {
                return new DatabaseConnectionEntry
                {
                    Id = "local-default",
                    Name = "Local phpMyAdmin",
                    Description = "127.0.0.1 · root",
                    DbType = DatabaseType.MySQL,
                    Host = "127.0.0.1",
                    Port = 3306,
                    Username = "root",
                    IsLocal = true,
                    IsFromProfile = false
                };
            }

            public static DatabaseConnectionEntry FromProfile(ConnectionProfile profile)
            {
                return new DatabaseConnectionEntry
                {
                    Id = profile.Id ?? Guid.NewGuid().ToString(),
                    Name = string.IsNullOrWhiteSpace(profile.Name) ? "Database Connection" : profile.Name,
                    Description = $"{profile.DbType} · {profile.DbHost}:{(profile.DbPort <= 0 ? 3306 : profile.DbPort)}",
                    DbType = profile.DbType == DatabaseType.None ? DatabaseType.MySQL : profile.DbType,
                    Host = string.IsNullOrWhiteSpace(profile.DbHost) ? "127.0.0.1" : profile.DbHost,
                    Port = profile.DbPort <= 0 ? 3306 : profile.DbPort,
                    Username = string.IsNullOrWhiteSpace(profile.DbUsername) ? "root" : profile.DbUsername,
                    Password = EncryptionService.Decrypt(profile.DbPassword),
                    DatabaseName = profile.DbName ?? string.Empty,
                    IsLocal = false,
                    IsFromProfile = true
                };
            }
        }
    }
}

