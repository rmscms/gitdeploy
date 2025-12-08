using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class ProcessMonitorWindow : MetroWindow
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<DatabaseConnectionEntry> _connections = new();
        private readonly ObservableCollection<DatabaseProcessInfo> _processes = new();
        private readonly DatabaseClient _client = new();
        private DatabaseConnectionEntry? _currentEntry;
        private bool _isBusy;
        private bool _isConnecting;
        private readonly DispatcherTimer _autoRefreshTimer = new();
        private int _autoRefreshSeconds;

        public ProcessMonitorWindow(DatabaseConnectionEntry? initialEntry = null)
        {
            try
            {
                InitializeComponent();
                
                if (ConnectionsCombo != null)
                {
                    ConnectionsCombo.ItemsSource = _connections;
                }
                
                if (ProcessGrid != null)
                {
                    ProcessGrid.ItemsSource = _processes;
                }

                _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;

                Loaded += async (_, _) =>
                {
                    try
                    {
                        await LoadConnectionsAsync(initialEntry);
                        
                        if (initialEntry != null && _connections.Count > 0)
                        {
                            var found = _connections.FirstOrDefault(c => c.Id == initialEntry.Id);
                            if (found != null && ConnectionsCombo != null)
                            {
                                ConnectionsCombo.SelectedItem = found;
                                await ConnectSelectedAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (StatusText != null)
                        {
                            StatusText.Text = $"Initialization error: {ex.Message}";
                        }
                    }
                };

                Closed += async (_, _) =>
                {
                    _autoRefreshTimer.Stop();
                    await _client.DisposeAsync();
                };
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to initialize Process Monitor: {ex.Message}\r\n\r\nStack: {ex.StackTrace}", 
                    "Process Monitor Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadConnectionsAsync(DatabaseConnectionEntry? initialEntry)
        {
            try
            {
                _connections.Clear();
                var loadedProfiles = _configService.LoadConnections() ?? new List<ConnectionProfile>();
                var savedProfiles = loadedProfiles
                    .Where(p => p != null && (p.DbType == DatabaseType.MySQL || p.DbType == DatabaseType.MariaDB))
                    .ToList();

                foreach (var profile in savedProfiles)
                {
                    var entry = DatabaseConnectionEntry.FromProfile(profile);
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                    {
                        _connections.Add(entry);
                    }
                }

                if (initialEntry != null && !string.IsNullOrWhiteSpace(initialEntry.Name) && !_connections.Any(c => c.Id == initialEntry.Id))
                {
                    _connections.Insert(0, initialEntry);
                }
                else if (initialEntry == null && _connections.Count == 0)
                {
                    var localDefault = DatabaseConnectionEntry.CreateLocalDefault();
                    if (localDefault != null)
                    {
                        _connections.Add(localDefault);
                    }
                }

                if (_connections.Count == 0 && StatusText != null)
                {
                    StatusText.Text = "No saved database profiles found. Create one in Connection Manager first.";
                }
                else if (ConnectionsCombo != null && ConnectionsCombo.SelectedItem == null && _connections.Count > 0)
                {
                    ConnectionsCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                if (StatusText != null)
                {
                    StatusText.Text = $"Failed to load saved connections: {ex.Message}";
                }
            }

            await Task.CompletedTask;
        }

        private async Task ConnectSelectedAsync()
        {
            try
            {
                if (ConnectionsCombo?.SelectedItem is not DatabaseConnectionEntry entry)
                {
                    if (StatusText != null)
                    {
                        StatusText.Text = "Select a connection to continue.";
                    }
                    return;
                }

                if (!entry.SupportsCurrentVersion)
                {
                    if (StatusText != null)
                    {
                        StatusText.Text = "Only MySQL/MariaDB connections are supported.";
                    }
                    return;
                }

                _isBusy = true;
                _isConnecting = true;
                UpdateUiState("Connecting...");
                
                await _client.DisconnectAsync();
                await _client.ConnectAsync(entry.ToConnectionInfo());
                
                _currentEntry = entry;
                
                if (StatusText != null)
                {
                    StatusText.Text = $"Connected to {entry.Name}.";
                }
                
                await RefreshProcessesAsync();
            }
            catch (Exception ex)
            {
                if (StatusText != null)
                {
                    StatusText.Text = $"Connection failed: {ex.Message}";
                }
            }
            finally
            {
                _isBusy = false;
                _isConnecting = false;
                UpdateUiState();
            }
        }

        private async Task RefreshProcessesAsync()
        {
            if (_isBusy)
            {
                return;
            }

            if (!_client.IsConnected)
            {
                if (StatusText != null)
                {
                    StatusText.Text = "Connect to a database first.";
                }
                return;
            }

            try
            {
                _isBusy = true;
                UpdateUiState("Loading process list...");
                
                var processes = await _client.GetProcessListAsync();
                _processes.Clear();
                
                foreach (var process in processes.OrderByDescending(p => p.TimeSeconds))
                {
                    _processes.Add(process);
                }

                if (StatusText != null)
                {
                    StatusText.Text = $"Updated {DateTime.Now:T} · {_processes.Count} sessions.";
                }
            }
            catch (Exception ex)
            {
                if (StatusText != null)
                {
                    StatusText.Text = $"Unable to load processlist: {ex.Message}";
                }
            }
            finally
            {
                _isBusy = false;
                UpdateUiState();
            }
        }

        private async Task DisconnectAsync()
        {
            try
            {
                _isBusy = true;
                _isConnecting = true;
                UpdateUiState("Disconnecting...");
                await _client.DisconnectAsync();
                _processes.Clear();
                
                if (StatusText != null)
                {
                    StatusText.Text = "Disconnected.";
                }
            }
            catch (Exception ex)
            {
                if (StatusText != null)
                {
                    StatusText.Text = $"Disconnect failed: {ex.Message}";
                }
            }
            finally
            {
                _isBusy = false;
                _isConnecting = false;
                UpdateUiState();
            }
        }

        private void UpdateUiState(string? busyMessage = null)
        {
            if (BusyBar != null)
            {
                BusyBar.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
            }
            
            if (!string.IsNullOrWhiteSpace(busyMessage) && StatusText != null)
            {
                StatusText.Text = busyMessage;
            }

            bool isConnected = _client.IsConnected;
            
            if (ConnectButton != null)
            {
                ConnectButton.Content = isConnected ? "Disconnect" : "Connect";
                ConnectButton.IsEnabled = !_isBusy;
            }
            
            if (RefreshButton != null)
            {
                RefreshButton.IsEnabled = isConnected && !_isBusy;
            }
            
            if (ConnectionsCombo != null)
            {
                ConnectionsCombo.IsEnabled = !_isBusy && !_isConnecting;
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client.IsConnected)
            {
                await DisconnectAsync();
            }
            else
            {
                await ConnectSelectedAsync();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshProcessesAsync();
        }

        private async void ReloadConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedId = (ConnectionsCombo?.SelectedItem as DatabaseConnectionEntry)?.Id;
            await LoadConnectionsAsync(_currentEntry);
            
            if (!string.IsNullOrWhiteSpace(selectedId) && ConnectionsCombo != null)
            {
                ConnectionsCombo.SelectedItem = _connections.FirstOrDefault(c => c.Id == selectedId);
            }
        }

        private async void ConnectionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Do nothing unless user clicks connect; avoids surprise reconnects.
            await Task.CompletedTask;
        }

        private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (_autoRefreshSeconds <= 0 || !_client.IsConnected || _isBusy)
            {
                return;
            }

            _ = RefreshProcessesAsync();
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IntervalCombo?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var seconds))
            {
                _autoRefreshSeconds = seconds;
            }
            else
            {
                _autoRefreshSeconds = 0;
            }

            if (_autoRefreshSeconds > 0)
            {
                _autoRefreshTimer.Interval = TimeSpan.FromSeconds(_autoRefreshSeconds);
                _autoRefreshTimer.Start();
                
                if (StatusText != null)
                {
                    StatusText.Text = $"Auto-refresh every {_autoRefreshSeconds} seconds.";
                }
            }
            else
            {
                _autoRefreshTimer.Stop();
                
                if (StatusText != null)
                {
                    StatusText.Text = "Auto-refresh disabled.";
                }
            }
        }
    }
}


