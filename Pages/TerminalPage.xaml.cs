using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GitDeployPro.Services;
using GitDeployPro.Models;
using GitDeployPro.Windows;
using GitDeployPro.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace GitDeployPro.Pages
{
    public partial class TerminalPage : Page
    {
        private ConfigurationService _configService;
        private string _currentProjectPath;
        private ObservableCollection<TerminalCommandPreset> _commandPresets = new();
        private List<ConnectionProfile> _allSshProfiles = new();
        private List<ConnectionProfile> _filteredProfiles = new();
        private List<TerminalInstance> _activeTerminals = new();
        
        private class TerminalInstance
        {
            public string ConnectionId { get; set; } = "";
            public string ConnectionName { get; set; } = "";
            public Controls.TerminalControl TerminalControl { get; set; } = null!;
            public Border Container { get; set; } = null!;
            public double DesiredWidth { get; set; }
            public double DesiredHeight { get; set; }
            public ConnectionProfile? Profile { get; set; } = null;
        }

        public TerminalPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            Loaded += TerminalPage_Loaded;
            Unloaded += TerminalPage_Unloaded;
        }

        private void TerminalPage_Loaded(object sender, RoutedEventArgs e)
        {
            var globalConfig = _configService.LoadGlobalConfig();
            
            if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
            {
                _currentProjectPath = globalConfig.LastProjectPath;
            }

            LoadCommandPresets();
            TerminalPresetStore.PresetsChanged += TerminalPresetStore_PresetsChanged;
            
            // Collapse main sidebar when Terminal page is loaded
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.SetSidebarCollapsed(true);
            }
            
            // Load Session Manager inline
            LoadSessionManager();
        }

        private void TerminalPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TerminalPresetStore.PresetsChanged -= TerminalPresetStore_PresetsChanged;
            
            // Restore main sidebar when Terminal page is unloaded
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.SetSidebarCollapsed(false);
            }
        }
        
        private void LoadSessionManager()
        {
            // Session Manager is already loaded via XAML
            // Hook up events for connection handling
            var sessionManager = FindName("SessionManagerContent") as Controls.SessionManagerControl;
            if (sessionManager == null)
            {
                // Try to find it in the visual tree
                sessionManager = FindVisualChild<Controls.SessionManagerControl>(this);
            }
            
            if (sessionManager != null)
            {
                sessionManager.ConnectionConnectRequested += SessionManager_ConnectionConnectRequested;
            }
        }
        
        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                {
                    return found;
                }
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }
        
        private async void SessionManager_ConnectionConnectRequested(object? sender, ConnectionProfile conn)
        {
            if (conn == null) return;
            
            try
            {
                // Create new terminal instance inside the grid (allow multiple per connection)
                var terminalControl = new Controls.TerminalControl();
                terminalControl.SetProjectPath(_currentProjectPath ?? "");
                terminalControl.DetachButton.Visibility = Visibility.Collapsed;
                
                // Create container with header and resize thumb
                var container = CreateTerminalContainer(conn.Name, terminalControl);
                
                // Add to active terminals list
                var instance = new TerminalInstance
                {
                    ConnectionId = conn.Id,
                    ConnectionName = conn.Name,
                    TerminalControl = terminalControl,
                    Container = container,
                    Profile = conn
                };
                _activeTerminals.Add(instance);
                
                // Add to grid and arrange
                TerminalsGrid.Children.Add(container);
                ArrangeTerminals();
                
                // Connect
                string password = EncryptionService.Decrypt(conn.Password);
                await terminalControl.ConnectAsync(conn.Host, conn.Username, password, conn.Port);
                
                // Update active count
                UpdateActiveTerminalsCount();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open terminal: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private Border CreateTerminalContainer(string title, Controls.TerminalControl terminalControl)
        {
            var container = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch
            };
            
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            
            // Header
            var header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(header, 0);
            
            var titleText = new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                FontSize = 12
            };
            var detachButton = new System.Windows.Controls.Button
            {
                Content = "⇱",
                Width = 30,
                Height = 28,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 5, 0)
            };
            
            var closeButton = new System.Windows.Controls.Button
            {
                Content = "✕",
                Width = 30,
                Height = 28,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 5, 0)
            };
            closeButton.Click += (s, e) =>
            {
                var instance = _activeTerminals.FirstOrDefault(t => t.Container == container);
                if (instance != null)
                {
                    CloseTerminal(instance);
                }
            };
            detachButton.Click += (s, e) =>
            {
                var instance = _activeTerminals.FirstOrDefault(t => t.Container == container);
                if (instance != null)
                {
                    // Open popup window for this connection
                    var win = new TerminalWindow(instance.Profile);
                    win.Show();
                    
                    CloseTerminal(instance);
                }
            };
            
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(detachButton, 1);
            Grid.SetColumn(closeButton, 2);
            headerGrid.Children.Add(titleText);
            headerGrid.Children.Add(detachButton);
            headerGrid.Children.Add(closeButton);
            header.Child = headerGrid;
            
            // Terminal control
            Grid.SetRow(terminalControl, 1);
            
            // Resize thumb (bottom-right)
            var resizeThumb = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = System.Windows.Input.Cursors.SizeNWSE,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60))
            };
            resizeThumb.DragDelta += (s, e) =>
            {
                var inst = _activeTerminals.FirstOrDefault(t => t.Container == container);
                if (inst != null)
                {
                    inst.DesiredWidth = Math.Max(250, container.ActualWidth + e.HorizontalChange);
                    inst.DesiredHeight = Math.Max(200, container.ActualHeight + e.VerticalChange);
                    container.Width = inst.DesiredWidth;
                    container.Height = inst.DesiredHeight;
                }
            };
            Grid.SetRow(resizeThumb, 1);
            Grid.SetColumn(resizeThumb, 0);
            Grid.SetColumnSpan(resizeThumb, 2);
            
            grid.Children.Add(header);
            grid.Children.Add(terminalControl);
            grid.Children.Add(resizeThumb);
            container.Child = grid;
            
            return container;
        }
        
        private void CloseTerminal(TerminalInstance instance)
        {
            TerminalsGrid.Children.Remove(instance.Container);
            _activeTerminals.Remove(instance);
            
            // Unregister from SessionManager
            SessionManagerControl.UnregisterActiveConnection(instance.ConnectionId);
            
            ArrangeTerminals();
            UpdateActiveTerminalsCount();
        }
        
        private void ArrangeTerminals()
        {
            int count = _activeTerminals.Count;
            if (count == 0)
            {
                TerminalsGrid.RowDefinitions.Clear();
                TerminalsGrid.ColumnDefinitions.Clear();
                return;
            }
            
            int rows, cols;
            
            // Determine grid layout
            if (count == 1)
            {
                rows = 1; cols = 1;
            }
            else if (count == 2)
            {
                rows = 1; cols = 2;
            }
            else if (count <= 4)
            {
                rows = 2; cols = 2;
            }
            else if (count <= 6)
            {
                rows = 2; cols = 3;
            }
            else if (count <= 9)
            {
                rows = 3; cols = 3;
            }
            else
            {
                rows = 3; cols = 4;
            }
            
            // Clear and rebuild grid definitions
            TerminalsGrid.RowDefinitions.Clear();
            TerminalsGrid.ColumnDefinitions.Clear();
            
            for (int i = 0; i < rows; i++)
            {
                TerminalsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            for (int i = 0; i < cols; i++)
            {
                TerminalsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            
            // Position terminals in grid
            for (int i = 0; i < count; i++)
            {
                int row = i / cols;
                int col = i % cols;
                
                var instance = _activeTerminals[i];
                var container = instance.Container;
                
                // Apply user resized size if present
                if (instance.DesiredWidth > 0)
                    container.Width = instance.DesiredWidth;
                else
                    container.ClearValue(FrameworkElement.WidthProperty);
                
                if (instance.DesiredHeight > 0)
                    container.Height = instance.DesiredHeight;
                else
                    container.ClearValue(FrameworkElement.HeightProperty);
                
                Grid.SetRow(container, row);
                Grid.SetColumn(container, col);
            }
        }
        
        private void UpdateActiveTerminalsCount()
        {
            ActiveTerminalsCount.Text = _activeTerminals.Count.ToString();
        }
        
        private bool _isResizing = false;
        
        private async void OpenLocalTerminalButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var terminalControl = new Controls.TerminalControl();
                terminalControl.SetProjectPath(_currentProjectPath ?? "");
                terminalControl.DetachButton.Visibility = Visibility.Collapsed;
                
                var container = CreateTerminalContainer("Local Terminal", terminalControl);
                
                var instance = new TerminalInstance
                {
                    ConnectionId = $"local-{Guid.NewGuid()}",
                    ConnectionName = "Local Terminal",
                    TerminalControl = terminalControl,
                    Container = container,
                    Profile = null
                };
                _activeTerminals.Add(instance);
                
                TerminalsGrid.Children.Add(container);
                ArrangeTerminals();
                
                await terminalControl.ConnectLocal();
                
                UpdateActiveTerminalsCount();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open local terminal: {ex.Message}", "Local Terminal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void Resizer_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isResizing = true;
            (sender as System.Windows.Controls.Border)?.CaptureMouse();
        }
        
        private void Resizer_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isResizing = false;
            (sender as System.Windows.Controls.Border)?.ReleaseMouseCapture();
        }
        
        private void Resizer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isResizing && sender is System.Windows.Controls.Border resizer)
            {
                var position = e.GetPosition(this);
                var newWidth = position.X;
                if (newWidth > 200 && newWidth < 600)
                {
                    SessionManagerColumn.Width = new GridLength(newWidth);
                }
            }
        }

        private void TerminalPresetStore_PresetsChanged()
        {
            Dispatcher.Invoke(LoadCommandPresets);
        }

        // Removed LoadSshProfiles, ApplyFilter, SearchBox_TextChanged, SshProfilesCombo_SelectionChanged, QuickConnect_Click, ManageProfiles_Click, SessionManager_Click
        // These are no longer needed as Session Manager handles connection selection

        private void LoadCommandPresets()
        {
            var previous = PresetComboBox?.SelectedValue?.ToString();
            _commandPresets = TerminalPresetStore.LoadPresets();
            if (PresetComboBox != null)
            {
                PresetComboBox.ItemsSource = _commandPresets;
                if (!string.IsNullOrEmpty(previous))
                {
                    var match = _commandPresets.FirstOrDefault(p => p.Id == previous);
                    if (match != null)
                    {
                        PresetComboBox.SelectedItem = match;
                    }
                }
                if (PresetComboBox.SelectedIndex == -1 && _commandPresets.Count > 0)
                {
                    PresetComboBox.SelectedIndex = 0;
                }
            }
        }

        private void AddPresetButton_Click(object sender, RoutedEventArgs e)
        {
            var title = (PresetTitleBox.Text ?? string.Empty).Trim();
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
            {
                System.Windows.MessageBox.Show("Please enter both a title and a command.", "Command Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preset = new TerminalCommandPreset
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Command = command
            };
            _commandPresets.Add(preset);
            TerminalPresetStore.SavePresets(_commandPresets);
            PresetTitleBox.Text = string.Empty;
            PresetCommandBox.Text = string.Empty;
            PresetComboBox.SelectedItem = preset;
        }

        private void UsePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox?.SelectedItem is TerminalCommandPreset preset)
            {
                SendPresetToTerminals(preset.Command);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox?.SelectedItem is TerminalCommandPreset preset)
            {
                var existing = _commandPresets.FirstOrDefault(p => p.Id == preset.Id);
                if (existing != null)
                {
                    _commandPresets.Remove(existing);
                    TerminalPresetStore.SavePresets(_commandPresets);
                }
            }
        }

        private void SendPresetToTerminals(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            if (BroadcastAllCheckBox?.IsChecked == true)
            {
                Controls.TerminalControl.BroadcastCommand(command);
            }
            else
            {
                if (_activeTerminals.Count > 0)
                {
                    _activeTerminals[0].TerminalControl.InjectCommandText(command);
                }
            }
        }

        private void DetachTerminalPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new TerminalPage(), "Terminal • Detached");
            window.Show();
        }
    }
}
