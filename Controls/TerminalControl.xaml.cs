using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Terminal;
using GitDeployPro.Services.Theme;
using GitDeployPro.Windows;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GitDeployPro.Controls
{
    public partial class TerminalControl : System.Windows.Controls.UserControl
    {
        private sealed class TerminalTargetOption
        {
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public bool IsLocal { get; init; }
            public ConnectionProfile? Profile { get; init; }

            public override string ToString() =>
                string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
        }

        public static readonly DependencyProperty ShowCommandBarProperty =
            DependencyProperty.Register(
                nameof(ShowCommandBar),
                typeof(bool),
                typeof(TerminalControl),
                new PropertyMetadata(true, OnShowCommandBarChanged));

        private static readonly HashSet<TerminalControl> _activeTerminals = new();
        private static string? _terminalHtmlTemplate;

        private readonly ConfigurationService _configService;
        private readonly SemaphoreSlim _webInitLock = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

        private ITerminalSession? _session;
        private TaskCompletionSource<bool>? _webReadyTcs;
        private bool _webReady;
        private bool _webInitFailed;
        private string _webInitFailureReason = string.Empty;
        private bool _isConnected;
        private bool _isLocal;
        private bool _typingEnabled = true;
        private bool _isDisconnecting;
        private bool _welcomeWritten;
        private int _currentColumns = 120;
        private int _currentRows = 35;
        private string? _projectPath;
        private DateTime _lastInterruptSentAt = DateTime.MinValue;
        private bool _remoteHistoryConfigured;
        private bool _disposed;
        private bool _suppressTerminalTargetSelectionChanged;
        private string _activeTerminalTargetId = string.Empty;
        private SavedCommandsWindow? _savedCommandsWindow;
        private string _presetsUiMode = SavedCommandsPanel.ModeDock;
        private bool _presetsUiOpen;
        private bool _suppressPresetsToggle;
        private bool _dockedPresetsWired;

        public bool ShowCommandBar
        {
            get => (bool)GetValue(ShowCommandBarProperty);
            set => SetValue(ShowCommandBarProperty, value);
        }

        public TerminalControl()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _presetsUiMode = SavedCommandsPanel.NormalizeMode(TerminalPresetStore.LoadUiMode());

            Loaded += TerminalControl_Loaded;
            Unloaded += TerminalControl_Unloaded;
            ThemeService.Instance.ThemeChanged += OnDeployThemeChanged;
            ApplyHostBackgroundFromTheme();
            WireDockedSavedCommandsPanel();
        }

        private void OnDeployThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnDeployThemeChanged(sender, e));
                return;
            }

            ApplyHostBackgroundFromTheme();
            DockedSavedCommandsPanel?.ApplyTheme();
            _savedCommandsWindow?.ApplyTheme();
            _ = ApplyXtermThemeAsync();
        }

        private void ApplyHostBackgroundFromTheme()
        {
            var host = TerminalHostGrid ?? TerminalWebView?.Parent as Grid;
            var color = ThemeService.Instance.GetTokenColor(
                "terminal.hostBackground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0C0C0C"));

            if (host != null)
            {
                host.Background = new SolidColorBrush(color);
            }

            if (TerminalWebView != null)
            {
                try
                {
                    TerminalWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
                        color.A, color.R, color.G, color.B);
                }
                catch
                {
                    // ignore invalid color conversion
                }
            }
        }

        private async Task ApplyXtermThemeAsync()
        {
            ApplyHostBackgroundFromTheme();

            var tokens = ThemeService.Instance.CurrentTokens;
            var theme = new Dictionary<string, string>
            {
                ["background"] = tokens.GetString("terminal.xterm.background", tokens.GetHex("terminal.xterm.background", "#0c0c0c")),
                ["foreground"] = tokens.GetString("terminal.xterm.foreground", tokens.GetHex("terminal.xterm.foreground", "#d4d4d4")),
                ["cursor"] = tokens.GetString("terminal.xterm.cursor", tokens.GetHex("terminal.xterm.cursor", "#00ff00")),
                ["selectionBackground"] = tokens.GetString("terminal.xterm.selectionBackground", "rgba(128,128,128,0.35)")
            };

            // Prefer resolved hex when Color map has values.
            if (tokens.Colors.ContainsKey("terminal.xterm.background"))
            {
                theme["background"] = tokens.GetHex("terminal.xterm.background", theme["background"]);
            }

            if (tokens.Colors.ContainsKey("terminal.xterm.foreground"))
            {
                theme["foreground"] = tokens.GetHex("terminal.xterm.foreground", theme["foreground"]);
            }

            if (tokens.Colors.ContainsKey("terminal.xterm.cursor"))
            {
                theme["cursor"] = tokens.GetHex("terminal.xterm.cursor", theme["cursor"]);
            }

            await PostTerminalMessageAsync(new { type = "setTheme", theme });
        }

        private static void OnShowCommandBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TerminalControl control)
            {
                control.ApplyCommandBarVisibility((bool)e.NewValue);
            }
        }

        private void ApplyCommandBarVisibility(bool visible)
        {
            if (CommandBarPanel == null || CommandBarRow == null)
            {
                return;
            }

            CommandBarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            CommandBarRow.Height = visible ? new GridLength(28) : new GridLength(0);
        }

        private void WireDockedSavedCommandsPanel()
        {
            if (DockedSavedCommandsPanel == null || _dockedPresetsWired)
            {
                return;
            }

            _dockedPresetsWired = true;
            DockedSavedCommandsPanel.RunCommand = command => InjectCommandText(command, execute: true);
            DockedSavedCommandsPanel.InsertCommand = command => InjectCommandText(command, execute: false);
            DockedSavedCommandsPanel.CloseRequested += (_, _) => SetPresetsUiOpen(false);
            DockedSavedCommandsPanel.PresentationModeRequested += (_, mode) => SetPresetsUiMode(mode, keepOpen: true);
            DockedSavedCommandsPanel.SetPresentationMode(_presetsUiMode);
        }

        private void PresetsDrawerToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressPresetsToggle)
            {
                return;
            }

            SetPresetsUiOpen(true);
        }

        private void PresetsDrawerToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressPresetsToggle)
            {
                return;
            }

            SetPresetsUiOpen(false);
        }

        private void SetPresetsUiMode(string mode, bool keepOpen)
        {
            _presetsUiMode = SavedCommandsPanel.NormalizeMode(mode);
            TerminalPresetStore.SaveUiMode(_presetsUiMode);
            DockedSavedCommandsPanel?.SetPresentationMode(_presetsUiMode);
            _savedCommandsWindow?.SetPresentationMode(_presetsUiMode);

            if (keepOpen || _presetsUiOpen)
            {
                ApplyPresetsPresentation(open: true);
            }
        }

        private void SetPresetsUiOpen(bool open)
        {
            _presetsUiOpen = open;
            ApplyPresetsPresentation(open);

            _suppressPresetsToggle = true;
            try
            {
                if (PresetsDrawerToggle != null && PresetsDrawerToggle.IsChecked != open)
                {
                    PresetsDrawerToggle.IsChecked = open;
                }
            }
            finally
            {
                _suppressPresetsToggle = false;
            }
        }

        private void ApplyPresetsPresentation(bool open)
        {
            var useFloat = open && _presetsUiMode == SavedCommandsPanel.ModeFloat;
            var useDock = open && !useFloat;

            ApplyDockedPresetsVisibility(useDock);

            if (useFloat)
            {
                ShowSavedCommandsWindow();
            }
            else
            {
                HideSavedCommandsWindow();
            }
        }

        private void ApplyDockedPresetsVisibility(bool visible)
        {
            if (PresetsDrawerPanel == null || PresetsDrawerRowDef == null)
            {
                return;
            }

            PresetsDrawerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            PresetsDrawerRowDef.Height = visible ? GridLength.Auto : new GridLength(0);
            if (visible)
            {
                DockedSavedCommandsPanel?.ApplyTheme();
                DockedSavedCommandsPanel?.Reload();
                DockedSavedCommandsPanel?.SetPresentationMode(_presetsUiMode);
            }

            UpdateLayout();
            _ = RequestTerminalFitAsync();
        }

        private void ShowSavedCommandsWindow()
        {
            if (_savedCommandsWindow == null)
            {
                _savedCommandsWindow = new SavedCommandsWindow
                {
                    RunCommand = command => InjectCommandText(command, execute: true),
                    InsertCommand = command => InjectCommandText(command, execute: false)
                };
                _savedCommandsWindow.CloseRequested += (_, _) => SetPresetsUiOpen(false);
                _savedCommandsWindow.PresentationModeRequested += (_, mode) => SetPresetsUiMode(mode, keepOpen: true);
                _savedCommandsWindow.Closed += SavedCommandsWindow_Closed;
            }

            _savedCommandsWindow.SetPresentationMode(SavedCommandsPanel.ModeFloat);
            _savedCommandsWindow.Reload();
            _savedCommandsWindow.ApplyTheme();

            if (!_savedCommandsWindow.IsVisible)
            {
                PositionSavedCommandsWindow(_savedCommandsWindow);
                WindowOwnerService.ShowOwned(_savedCommandsWindow, this, centerOnOwner: false);
            }
            else
            {
                _savedCommandsWindow.Activate();
            }
        }

        private void HideSavedCommandsWindow()
        {
            if (_savedCommandsWindow?.IsVisible == true)
            {
                _savedCommandsWindow.Hide();
            }
        }

        private void CloseSavedCommandsWindow()
        {
            if (_savedCommandsWindow == null)
            {
                return;
            }

            var window = _savedCommandsWindow;
            _savedCommandsWindow = null;
            _presetsUiOpen = false;
            window.Closed -= SavedCommandsWindow_Closed;
            try
            {
                window.Close();
            }
            catch
            {
                // ignore
            }
        }

        private void SavedCommandsWindow_Closed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _savedCommandsWindow))
            {
                _savedCommandsWindow = null;
            }

            if (_presetsUiOpen && _presetsUiMode == SavedCommandsPanel.ModeFloat)
            {
                SetPresetsUiOpen(false);
            }
        }

        private void PositionSavedCommandsWindow(Window window)
        {
            try
            {
                var owner = WindowOwnerService.ResolveOwner(this);
                if (owner != null)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = owner.Left + Math.Max(24, owner.ActualWidth - window.Width - 36);
                    window.Top = owner.Top + 72;
                    return;
                }

                var screen = System.Windows.SystemParameters.WorkArea;
                window.Left = screen.Right - window.Width - 24;
                window.Top = screen.Top + 72;
            }
            catch
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }

        private async Task RequestTerminalFitAsync()
        {
            try
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                await PostTerminalMessageAsync(new { type = "fit" });
            }
            catch
            {
                // Ignore fit races while WebView is still initializing.
            }
        }

        public void SetProjectPath(string path)
        {
            _projectPath = path;
            if (IsLoaded)
            {
                LoadTerminalTargets();
            }
        }

        public static void BroadcastCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            List<TerminalControl> snapshot;
            lock (_activeTerminals)
            {
                snapshot = _activeTerminals.ToList();
            }

            foreach (var terminal in snapshot)
            {
                terminal.Dispatcher.Invoke(() => terminal.InjectCommandText(command));
            }
        }

        public async Task ConnectLocal(string shell = "cmd.exe")
        {
            using var scope = PerformanceSampler.Instance.BeginScope("terminal", "connect-local", shell);
            await EnsureWebViewReadyAsync();
            if (_webInitFailed)
            {
                return;
            }
            await DisconnectAsync(includeCloseMessage: false);

            SetConnectingStatus("Starting local shell...");

            var workingDirectory = ResolveWorkingDirectory();
            var forceLegacy = IsLegacyBackendForced();
            Exception? conPtyFailure = null;

            if (!forceLegacy)
            {
                var conPtySession = new ConPtyTerminalSession(shell, workingDirectory, _currentColumns, _currentRows);
                try
                {
                    await StartSessionAsync(conPtySession, isLocal: true, statusText: $"Local ({shell})");
                    return;
                }
                catch (Exception ex)
                {
                    conPtyFailure = ex;
                    await conPtySession.DisposeAsync();
                }
            }

            var fallbackSession = new RedirectedProcessTerminalSession(shell, workingDirectory);
            try
            {
                await StartSessionAsync(fallbackSession, isLocal: true, statusText: $"Local ({shell}) [fallback]");

                var reasonText = forceLegacy
                    ? "ConPTY disabled by feature flag (GDP_TERMINAL_LOCAL_BACKEND=legacy)."
                    : $"ConPTY failed, fallback enabled: {conPtyFailure?.Message}";
                await WriteToTerminalAsync($"\r\n[warn] {reasonText}\r\n");
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                await fallbackSession.DisposeAsync();
                await HandleConnectionFailureAsync(ex, "Failed to start local terminal");
            }
        }

        public async Task ConnectAsync(string host, string user, string password, int port)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("terminal", "connect-ssh", $"{user}@{host}:{port}");
            await EnsureWebViewReadyAsync();
            if (_webInitFailed)
            {
                return;
            }
            await DisconnectAsync(includeCloseMessage: false);

            SetConnectingStatus($"Connecting to {host}...");

            var session = new SshTerminalSession(host, user, password, port, _currentColumns, _currentRows);
            try
            {
                await StartSessionAsync(session, isLocal: false, statusText: $"Connected to {host}");
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                await session.DisposeAsync();
                await HandleConnectionFailureAsync(ex, "Connection failed");
            }
        }

        public void InjectCommandText(string command, bool execute = true)
        {
            _ = InjectCommandTextAsync(command, execute);
        }

        private async void TerminalControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            lock (_activeTerminals)
            {
                _activeTerminals.Add(this);
            }

            ApplyCommandBarVisibility(ShowCommandBar);
            WireDockedSavedCommandsPanel();
            DockedSavedCommandsPanel?.SetPresentationMode(_presetsUiMode);

            if (ShowCommandBar)
            {
                if (string.IsNullOrWhiteSpace(_projectPath))
                {
                    _projectPath = _configService.LoadGlobalConfig().LastProjectPath;
                }

                LoadTerminalTargets();
                ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;
                ConfigurationService.ConnectionsChanged += OnConnectionsChanged;
            }

            try
            {
                await EnsureWebViewReadyAsync();
                if (_webInitFailed)
                {
                    return;
                }
                await ApplyTerminalSettingsAsync();
                await WriteWelcomeAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Terminal init failed";
                StatusIndicator.Background = System.Windows.Media.Brushes.Red;
                MarkWebInitFailure($"Terminal load exception: {ex.Message}");
            }
        }

        private async void TerminalControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ThemeService.Instance.ThemeChanged -= OnDeployThemeChanged;
            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;
            CloseSavedCommandsWindow();

            lock (_activeTerminals)
            {
                _activeTerminals.Remove(this);
            }

            await DisconnectAsync(includeCloseMessage: false);
            ReleaseWebViewBridge();
            _welcomeWritten = false;
            _remoteHistoryConfigured = false;
        }

        private void OnConnectionsChanged(object? sender, EventArgs e)
        {
            if (!ShowCommandBar || _disposed)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(LoadTerminalTargets);
                return;
            }

            LoadTerminalTargets();
        }

        private void LoadTerminalTargets()
        {
            if (TerminalTargetCombo == null)
            {
                return;
            }

            var previousId = ((TerminalTargetCombo.SelectedItem as ComboBoxItem)?.Tag as TerminalTargetOption)?.Id
                             ?? (TerminalTargetCombo.SelectedItem as TerminalTargetOption)?.Id;
            var items = new List<TerminalTargetOption>
            {
                new()
                {
                    Id = "local",
                    DisplayName = "Local Terminal",
                    IsLocal = true
                }
            };

            try
            {
                var profiles = _configService.LoadConnections()
                    .Where(p => p != null && p.UseSSH && !string.IsNullOrWhiteSpace(p.Host))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var profile in profiles)
                {
                    items.Add(new TerminalTargetOption
                    {
                        Id = profile.Id,
                        DisplayName = string.IsNullOrWhiteSpace(profile.Name)
                            ? $"{profile.Username}@{profile.Host}"
                            : profile.Name,
                        IsLocal = false,
                        Profile = profile
                    });
                }

                // Also include project-assigned SSH if missing from profiles list.
                if (!string.IsNullOrWhiteSpace(_projectPath))
                {
                    var project = _configService.LoadProjectConfig(_projectPath);
                    if (project.UseSSH && !string.IsNullOrWhiteSpace(project.FtpHost))
                    {
                        var projectId = "project-ssh";
                        if (items.All(i => !string.Equals(i.DisplayName, $"Project SSH ({project.FtpHost})", StringComparison.OrdinalIgnoreCase)))
                        {
                            items.Add(new TerminalTargetOption
                            {
                                Id = projectId,
                                DisplayName = $"Project SSH ({project.FtpHost})",
                                IsLocal = false,
                                Profile = new ConnectionProfile
                                {
                                    Id = projectId,
                                    Name = "Project SSH",
                                    Host = project.FtpHost,
                                    Username = project.FtpUsername,
                                    Password = project.FtpPassword,
                                    Port = project.FtpPort,
                                    UseSSH = true
                                }
                            });
                        }
                    }
                }
            }
            catch
            {
                // Keep at least Local.
            }

            // Use ComboBoxItem.Content so custom ComboBox templates always show the label.
            _suppressTerminalTargetSelectionChanged = true;
            try
            {
                TerminalTargetCombo.Items.Clear();
                ComboBoxItem? selectedItem = null;
                foreach (var option in items)
                {
                    var boxItem = new ComboBoxItem
                    {
                        Content = option.DisplayName,
                        Tag = option,
                        ToolTip = option.DisplayName
                    };
                    TerminalTargetCombo.Items.Add(boxItem);
                    if (selectedItem == null &&
                        (string.Equals(option.Id, previousId, StringComparison.OrdinalIgnoreCase)
                         || (!option.IsLocal && previousId == null)))
                    {
                        selectedItem = boxItem;
                    }
                }

                if (selectedItem == null && TerminalTargetCombo.Items.Count > 0)
                {
                    selectedItem = (ComboBoxItem)TerminalTargetCombo.Items[0];
                }

                TerminalTargetCombo.SelectedItem = selectedItem;
            }
            finally
            {
                _suppressTerminalTargetSelectionChanged = false;
            }
        }

        private async void TerminalTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTerminalTargetSelectionChanged || _disposed || !ShowCommandBar)
            {
                return;
            }

            var target = (TerminalTargetCombo?.SelectedItem as ComboBoxItem)?.Tag as TerminalTargetOption
                         ?? TerminalTargetCombo?.SelectedItem as TerminalTargetOption;
            if (target == null || string.IsNullOrWhiteSpace(target.Id))
            {
                return;
            }

            if (_isConnected &&
                string.Equals(_activeTerminalTargetId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ConnectSelectedTargetAsync();
        }

        private async Task ConnectSelectedTargetAsync()
        {
            try
            {
                var target = (TerminalTargetCombo?.SelectedItem as ComboBoxItem)?.Tag as TerminalTargetOption
                             ?? TerminalTargetCombo?.SelectedItem as TerminalTargetOption;
                if (target != null)
                {
                    if (target.IsLocal)
                    {
                        await ConnectLocal();
                        _activeTerminalTargetId = _isConnected ? target.Id : string.Empty;
                        return;
                    }

                    if (target.Profile != null)
                    {
                        await ConnectAsync(
                            target.Profile.Host,
                            target.Profile.Username,
                            EncryptionService.Decrypt(target.Profile.Password),
                            target.Profile.Port);
                        _activeTerminalTargetId = _isConnected ? target.Id : string.Empty;
                        return;
                    }
                }

                await ConnectAsync();
                _activeTerminalTargetId = _isConnected ? (target?.Id ?? "project-ssh") : string.Empty;
            }
            catch (Exception ex)
            {
                _activeTerminalTargetId = string.Empty;
                await HandleConnectionFailureAsync(ex, "Unable to connect selected terminal");
            }
        }

        private async Task ConnectAsync()
        {
            try
            {
                var config = _configService.LoadProjectConfig(_projectPath);
                if (string.IsNullOrWhiteSpace(config.FtpHost) || !config.UseSSH)
                {
                    await WriteToTerminalAsync("\r\n[error] SSH is not configured for this project.\r\n");
                    return;
                }

                await ConnectAsync(
                    config.FtpHost,
                    config.FtpUsername,
                    EncryptionService.Decrypt(config.FtpPassword),
                    config.FtpPort);
            }
            catch (Exception ex)
            {
                await HandleConnectionFailureAsync(ex, "Unable to load SSH configuration");
            }
        }

        private async Task StartSessionAsync(ITerminalSession session, bool isLocal, string statusText)
        {
            AttachSession(session);
            _session = session;
            _isDisconnecting = false;
            _remoteHistoryConfigured = false;

            try
            {
                await session.StartAsync();
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("Terminal session reported disconnected after start.");
                }

                _isConnected = true;
                _isLocal = isLocal;
                SetConnectedStatus(statusText);
                await FocusTerminalAsync();
                if (!isLocal)
                {
                    await ConfigureRemoteHistorySyncAsync();
                }
                await PostTerminalMessageAsync(new { type = "focus" });
            }
            catch
            {
                DetachSession(session);
                _session = null;
                _isConnected = false;
                throw;
            }
        }

        private async Task DisconnectAsync(bool includeCloseMessage = true)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("terminal", "disconnect", includeCloseMessage ? "normal" : "silent");
            if (_isDisconnecting)
            {
                return;
            }

            _isDisconnecting = true;

            var session = _session;
            _session = null;

            if (session != null)
            {
                DetachSession(session);
                try
                {
                    await session.StopAsync();
                }
                catch
                {
                    // Ignore shutdown errors.
                }

                try
                {
                    await session.DisposeAsync();
                }
                catch
                {
                    // Ignore shutdown errors.
                }
            }

            _isConnected = false;
            _isLocal = false;
            _activeTerminalTargetId = string.Empty;
            SetDisconnectedStatus();
            _isDisconnecting = false;

            if (includeCloseMessage)
            {
                await WriteToTerminalAsync("\r\nSession closed.\r\n");
            }
        }

        public async Task DisposeTerminalAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Loaded -= TerminalControl_Loaded;
            Unloaded -= TerminalControl_Unloaded;
            ConfigurationService.ConnectionsChanged -= OnConnectionsChanged;
            CloseSavedCommandsWindow();

            lock (_activeTerminals)
            {
                _activeTerminals.Remove(this);
            }

            await DisconnectAsync(includeCloseMessage: false);
            ReleaseWebViewBridge();
        }

        private void ReleaseWebViewBridge()
        {
            try
            {
                if (TerminalWebView?.CoreWebView2 != null)
                {
                    TerminalWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    TerminalWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                }
            }
            catch
            {
                // Ignore teardown errors.
            }

            _webReady = false;
            _webReadyTcs = null;
        }

        private async Task EnsureWebViewReadyAsync()
        {
            if (_webInitFailed)
            {
                return;
            }

            if (_webReady && TerminalWebView?.CoreWebView2 != null)
            {
                return;
            }

            TaskCompletionSource<bool>? readyTcs = null;
            await _webInitLock.WaitAsync();
            try
            {
                if (_webInitFailed)
                {
                    return;
                }

                if (_webReady && TerminalWebView?.CoreWebView2 != null)
                {
                    return;
                }

                await EnsureTerminalTemplateAsync();

                await TerminalWebView.EnsureCoreWebView2Async();

                TerminalWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                TerminalWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                TerminalWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                TerminalWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                // Let the page handle Ctrl+C / Ctrl+V instead of Edge browser accelerators.
                TerminalWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                TerminalWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                TerminalWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                TerminalWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                TerminalWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

                if (_webReadyTcs == null || _webReadyTcs.Task.IsCompleted)
                {
                    _webReady = false;
                    _webReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TerminalWebView.CoreWebView2.NavigateToString(_terminalHtmlTemplate ?? "<html><body>Terminal template missing.</body></html>");
                }

                readyTcs = _webReadyTcs;
            }
            catch (Exception ex)
            {
                MarkWebInitFailure($"WebView init failed: {ex.Message}");
                return;
            }
            finally
            {
                _webInitLock.Release();
            }

            if (readyTcs != null)
            {
                try
                {
                    var isReady = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    if (!isReady)
                    {
                        MarkWebInitFailure("Terminal renderer navigation failed.");
                    }
                }
                catch (TimeoutException)
                {
                    MarkWebInitFailure("Terminal renderer handshake timed out.");
                }
                catch (Exception ex)
                {
                    MarkWebInitFailure($"Terminal renderer failed: {ex.Message}");
                }
            }
        }

        private async Task WriteWelcomeAsync()
        {
            if (_welcomeWritten)
            {
                return;
            }

            await WriteToTerminalAsync("GitDeploy Pro Terminal [xterm.js + ConPTY]\r\nReady to connect...\r\n\r\n");
            _welcomeWritten = true;
        }

        private void AttachSession(ITerminalSession session)
        {
            session.OutputReceived += Session_OutputReceived;
            session.SessionClosed += Session_SessionClosed;
        }

        private void DetachSession(ITerminalSession session)
        {
            session.OutputReceived -= Session_OutputReceived;
            session.SessionClosed -= Session_SessionClosed;
        }

        private void Session_OutputReceived(string output)
        {
            var normalized = NormalizeOutput(output);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await WriteToTerminalAsync(normalized);
            });
        }

        private void Session_SessionClosed()
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_isDisconnecting || !_isConnected)
                {
                    return;
                }

                await DisconnectAsync();
            });
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_session == null && !_webReady && string.IsNullOrWhiteSpace(e.WebMessageAsJson))
            {
                return;
            }

            try
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                await HandleWebMessageAsync(message.RootElement);
            }
            catch
            {
                // Ignore malformed bridge messages.
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                _webReadyTcs?.TrySetResult(false);
            }
        }

        private async Task HandleWebMessageAsync(JsonElement message)
        {
            if (!message.TryGetProperty("type", out var typeProperty))
            {
                return;
            }

            var messageType = typeProperty.GetString() ?? string.Empty;
            switch (messageType)
            {
                case "ready":
                    _webReady = true;
                    _webReadyTcs?.TrySetResult(true);
                    await ApplyTerminalSettingsAsync();
                    await WriteWelcomeAsync();
                    break;

                case "input":
                    if (_typingEnabled && _session != null && _isConnected && message.TryGetProperty("data", out var dataProperty))
                    {
                        var input = dataProperty.GetString();
                        if (!string.IsNullOrEmpty(input))
                        {
                            if (input == "\u0003")
                            {
                                await TrySendInterruptAsync();
                            }
                            else
                            {
                                await _session.WriteAsync(input);
                            }
                        }
                    }
                    break;

                case "interrupt":
                    if (_typingEnabled && _session != null && _isConnected)
                    {
                        await TrySendInterruptAsync();
                    }
                    break;

                case "resize":
                    if (message.TryGetProperty("cols", out var colsProperty) &&
                        message.TryGetProperty("rows", out var rowsProperty) &&
                        colsProperty.TryGetInt32(out var cols) &&
                        rowsProperty.TryGetInt32(out var rows))
                    {
                        _currentColumns = Math.Max(20, cols);
                        _currentRows = Math.Max(5, rows);
                        if (_session != null && _isConnected)
                        {
                            await _session.ResizeAsync(_currentColumns, _currentRows);
                        }
                    }
                    break;

                case "copy":
                    if (message.TryGetProperty("text", out var textProperty))
                    {
                        var text = textProperty.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            System.Windows.Clipboard.SetText(text);
                        }
                    }
                    break;

                case "pasteRequest":
                    if (_typingEnabled && System.Windows.Clipboard.ContainsText())
                    {
                        var clipboardText = System.Windows.Clipboard.GetText();
                        await PostTerminalMessageAsync(new { type = "paste", text = clipboardText });
                    }
                    break;
            }
        }

        private async Task WriteToTerminalAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            await EnsureWebViewReadyAsync();
            if (!_webReady || _webInitFailed)
            {
                return;
            }
            await PostTerminalMessageAsync(new { type = "write", data = text });
        }

        private Task PostTerminalMessageAsync(object payload)
        {
            if (!_webReady || TerminalWebView?.CoreWebView2 == null)
            {
                return Task.CompletedTask;
            }

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            TerminalWebView.CoreWebView2.PostWebMessageAsJson(json);
            return Task.CompletedTask;
        }

        private async Task ApplyTerminalSettingsAsync()
        {
            await PostTerminalMessageAsync(new { type = "setTypingEnabled", enabled = _typingEnabled });
            await PostTerminalMessageAsync(new { type = "setFontSize", value = GetCurrentFontSize() });
            await PostTerminalMessageAsync(new { type = "setForeground", value = GetCurrentTextColorHex() });
            await ApplyXtermThemeAsync();
            TypingOverlay.Visibility = _typingEnabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private static async Task EnsureTerminalTemplateAsync()
        {
            if (!string.IsNullOrEmpty(_terminalHtmlTemplate))
            {
                return;
            }

            var filePath = Path.Combine(AppContext.BaseDirectory, "Resources", "TerminalHost.html");
            if (File.Exists(filePath))
            {
                _terminalHtmlTemplate = await File.ReadAllTextAsync(filePath);
                return;
            }

            var assembly = typeof(TerminalControl).Assembly;
            const string embeddedResource = "GitDeployPro.Resources.TerminalHost.html";
            await using var resourceStream = assembly.GetManifestResourceStream(embeddedResource);
            if (resourceStream == null)
            {
                throw new FileNotFoundException("TerminalHost.html was not found in resources.", filePath);
            }

            using var reader = new StreamReader(resourceStream);
            _terminalHtmlTemplate = await reader.ReadToEndAsync();
        }

        private static bool IsLegacyBackendForced()
        {
            var mode = Environment.GetEnvironmentVariable("GDP_TERMINAL_LOCAL_BACKEND");
            return string.Equals(mode, "legacy", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveWorkingDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_projectPath) && Directory.Exists(_projectPath))
            {
                return _projectPath;
            }

            return "C:\\";
        }

        private double GetCurrentFontSize()
        {
            if (FontSizeCombo.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Content?.ToString(), out var size))
            {
                return size;
            }

            return 14;
        }

        private string GetCurrentTextColorHex()
        {
            if (TextColorCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }

            return "#D4D4D4";
        }

        private static string NormalizeOutput(string output)
        {
            return output.Replace("\0", string.Empty);
        }

        private async Task TrySendInterruptAsync()
        {
            if (_session == null || !_isConnected)
            {
                return;
            }

            var now = AppTimeService.UtcNow;
            if ((now - _lastInterruptSentAt).TotalMilliseconds < 220)
            {
                return;
            }

            _lastInterruptSentAt = now;
            await _session.SendInterruptAsync();
        }

        private async Task ConfigureRemoteHistorySyncAsync()
        {
            if (_remoteHistoryConfigured || _session == null || !_isConnected || _isLocal)
            {
                return;
            }

            const string historySyncBootstrap =
                "if [ -n \"$BASH_VERSION\" ]; then shopt -s histappend 2>/dev/null; export HISTCONTROL=ignoredups:erasedups; PROMPT_COMMAND=\"history -a; history -n; ${PROMPT_COMMAND:-}\"; history -n; fi; " +
                "if [ -n \"$ZSH_VERSION\" ]; then setopt APPEND_HISTORY SHARE_HISTORY INC_APPEND_HISTORY 2>/dev/null; fi\r";

            try
            {
                await _session.WriteAsync(historySyncBootstrap);
                _remoteHistoryConfigured = true;
            }
            catch
            {
                // Keep terminal usable even when history bootstrap fails.
            }
        }

        private async Task HandleConnectionFailureAsync(Exception exception, string caption)
        {
            SetDisconnectedStatus();
            if (_webInitFailed)
            {
                StatusText.Text = "Terminal init failed";
                return;
            }

            var message = exception.Message ?? string.Empty;
            if (exception is OperationCanceledException ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unable to connect", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no route to host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase))
            {
                message = $"{message} Host unreachable — check VPN/network.";
            }

            await WriteToTerminalAsync($"\r\n[error] {caption}: {message}\r\n");
            StatusText.Text = $"{caption}: {message}";
            StatusIndicator.Background = ThemeService.Instance.GetTokenBrush(
                "terminal.statusError",
                System.Windows.Media.Colors.Red);
        }

        private void SetConnectingStatus(string text)
        {
            StatusText.Text = text;
            StatusIndicator.Background = ThemeService.Instance.GetTokenBrush(
                "terminal.statusConnecting",
                System.Windows.Media.Colors.OrangeRed);
        }

        private void SetConnectedStatus(string text)
        {
            StatusText.Text = text;
            StatusIndicator.Background = ThemeService.Instance.GetTokenBrush(
                "terminal.statusConnected",
                System.Windows.Media.Colors.LimeGreen);
        }

        private void SetDisconnectedStatus()
        {
            StatusText.Text = "Disconnected";
            StatusIndicator.Background = ThemeService.Instance.GetTokenBrush(
                "terminal.statusDisconnected",
                System.Windows.Media.Colors.Gray);
        }

        private async Task InjectCommandTextAsync(string command, bool execute = true)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            // Normalize trailing newlines; Enter is optional (insert vs run).
            var payload = command.TrimEnd('\r', '\n');

            if (_session == null || !_isConnected)
            {
                await WriteToTerminalAsync(execute ? $"\r\n> {payload}\r\n" : $"\r\n> {payload}");
                await FocusTerminalAsync();
                return;
            }

            await _session.WriteAsync(payload);
            if (execute)
            {
                await Task.Delay(15);
                await _session.WriteAsync("\r");
            }

            await FocusTerminalAsync();
        }

        public Task FocusTerminalAsync()
        {
            return PostTerminalMessageAsync(new { type = "focus" });
        }

        private void DetachButton_Click(object sender, RoutedEventArgs e)
        {
            // Open an extra floating terminal window (independent session).
            var window = new TerminalWindow(_projectPath ?? string.Empty)
            {
                Title = "Terminal • Float"
            };
            WindowOwnerService.ShowOwned(window, this, centerOnOwner: false);
        }

        private async void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            await PostTerminalMessageAsync(new { type = "clear" });
            await WriteToTerminalAsync("Terminal cleared.\r\n");
        }

        private async void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await PostTerminalMessageAsync(new { type = "setFontSize", value = GetCurrentFontSize() });
        }

        private async void TextColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await PostTerminalMessageAsync(new { type = "setForeground", value = GetCurrentTextColorHex() });
        }

        private async void TypeToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            _typingEnabled = true;
            if (TypeToggleButton != null)
            {
                TypeToggleButton.ToolTip = "Focus/Type (Enabled)";
            }
            if (TypingOverlay != null)
            {
                TypingOverlay.Visibility = Visibility.Collapsed;
            }
            await PostTerminalMessageAsync(new { type = "setTypingEnabled", enabled = true });
            await FocusTerminalAsync();
        }

        private async void TypeToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            _typingEnabled = false;
            if (TypeToggleButton != null)
            {
                TypeToggleButton.ToolTip = "Focus/Type (Disabled)";
            }
            if (TypingOverlay != null)
            {
                TypingOverlay.Visibility = Visibility.Visible;
            }
            await PostTerminalMessageAsync(new { type = "setTypingEnabled", enabled = false });
            await FocusTerminalAsync();
        }

        private void MarkWebInitFailure(string reason)
        {
            _webInitFailed = true;
            _webReady = false;
            _webInitFailureReason = reason;
            StatusText.Text = "Terminal init failed";
            StatusText.ToolTip = _webInitFailureReason;
            StatusIndicator.Background = System.Windows.Media.Brushes.Red;
        }
    }
}
