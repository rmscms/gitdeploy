using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Terminal;
using GitDeployPro.Windows;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private ObservableCollection<TerminalCommandPreset> _commandPresets = new();

        public bool ShowCommandBar
        {
            get => (bool)GetValue(ShowCommandBarProperty);
            set => SetValue(ShowCommandBarProperty, value);
        }

        public TerminalControl()
        {
            InitializeComponent();
            _configService = new ConfigurationService();

            Loaded += TerminalControl_Loaded;
            Unloaded += TerminalControl_Unloaded;
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

            if (!visible && PresetEditToggle != null)
            {
                PresetEditToggle.IsChecked = false;
                ApplyPresetEditVisibility(false);
            }
        }

        private void PresetEditToggle_Checked(object sender, RoutedEventArgs e) => ApplyPresetEditVisibility(true);

        private void PresetEditToggle_Unchecked(object sender, RoutedEventArgs e) => ApplyPresetEditVisibility(false);

        private void ApplyPresetEditVisibility(bool visible)
        {
            if (PresetEditPanel == null || PresetEditRowDef == null)
            {
                return;
            }

            PresetEditPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            PresetEditRowDef.Height = visible ? GridLength.Auto : new GridLength(0);
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

        public void InjectCommandText(string command)
        {
            _ = InjectCommandTextAsync(command);
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
            if (ShowCommandBar)
            {
                if (string.IsNullOrWhiteSpace(_projectPath))
                {
                    _projectPath = _configService.LoadGlobalConfig().LastProjectPath;
                }

                LoadTerminalTargets();
                LoadCommandPresets();
                TerminalPresetStore.PresetsChanged += TerminalPresetStore_PresetsChanged;
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
            TerminalPresetStore.PresetsChanged -= TerminalPresetStore_PresetsChanged;

            lock (_activeTerminals)
            {
                _activeTerminals.Remove(this);
            }

            await DisconnectAsync(includeCloseMessage: false);
            ReleaseWebViewBridge();
            _welcomeWritten = false;
            _remoteHistoryConfigured = false;
        }

        private void TerminalPresetStore_PresetsChanged()
        {
            Dispatcher.Invoke(LoadCommandPresets);
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

        private void LoadCommandPresets()
        {
            if (PresetComboBox == null)
            {
                return;
            }

            var previous = PresetComboBox.SelectedValue?.ToString();
            _commandPresets = TerminalPresetStore.LoadPresets();
            PresetComboBox.ItemsSource = _commandPresets;
            if (!string.IsNullOrEmpty(previous))
            {
                var match = _commandPresets.FirstOrDefault(p => p.Id == previous);
                if (match != null)
                {
                    PresetComboBox.SelectedItem = match;
                }
            }

            if (PresetComboBox.SelectedIndex < 0 && _commandPresets.Count > 0)
            {
                PresetComboBox.SelectedIndex = 0;
            }
        }

        private void InsertPreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is TerminalCommandPreset preset &&
                !string.IsNullOrWhiteSpace(preset.Command))
            {
                InjectCommandText(preset.Command);
                return;
            }

            var typed = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(typed))
            {
                InjectCommandText(typed);
            }
        }

        private void SendCommand_Click(object sender, RoutedEventArgs e)
        {
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                if (PresetComboBox.SelectedItem is TerminalCommandPreset preset)
                {
                    command = preset.Command;
                }
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            InjectCommandText(command);
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var title = (PresetTitleBox.Text ?? string.Empty).Trim();
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
            {
                ModernMessageBox.Show(
                    "Please enter both a title and a command.",
                    "Command Presets",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is not TerminalCommandPreset preset)
            {
                return;
            }

            var existing = _commandPresets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            _commandPresets.Remove(existing);
            TerminalPresetStore.SavePresets(_commandPresets);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                await DisconnectAsync();
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
                        return;
                    }

                    if (target.Profile != null)
                    {
                        await ConnectAsync(
                            target.Profile.Host,
                            target.Profile.Username,
                            EncryptionService.Decrypt(target.Profile.Password),
                            target.Profile.Port);
                        return;
                    }
                }

                await ConnectAsync();
            }
            catch (Exception ex)
            {
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
                _isConnected = true;
                _isLocal = isLocal;
                SetConnectedStatus(statusText);
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

            await WriteToTerminalAsync($"\r\n[error] {caption}: {exception.Message}\r\n");
        }

        private void SetConnectingStatus(string text)
        {
            StatusText.Text = text;
            StatusIndicator.Background = System.Windows.Media.Brushes.Orange;
            ConnectButton.IsEnabled = false;
        }

        private void SetConnectedStatus(string text)
        {
            StatusText.Text = text;
            StatusIndicator.Background = System.Windows.Media.Brushes.LimeGreen;
            ConnectButton.Content = "Disconnect";
            ConnectButton.Background = System.Windows.Media.Brushes.DarkRed;
            ConnectButton.IsEnabled = true;
        }

        private void SetDisconnectedStatus()
        {
            StatusText.Text = "Disconnected";
            StatusIndicator.Background = System.Windows.Media.Brushes.Gray;
            ConnectButton.Content = "Connect";
            ConnectButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204));
            ConnectButton.IsEnabled = true;
        }

        private async Task InjectCommandTextAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            // Normalize so we always send a single Enter after the command body.
            var payload = command.TrimEnd('\r', '\n');

            if (_session == null || !_isConnected)
            {
                await WriteToTerminalAsync($"\r\n> {payload}\r\n");
                await FocusTerminalAsync();
                return;
            }

            // Write body and Enter separately (matches typed Enter from xterm = \r).
            await _session.WriteAsync(payload);
            await Task.Delay(15);
            await _session.WriteAsync("\r");
            await FocusTerminalAsync();
        }

        public Task FocusTerminalAsync()
        {
            return PostTerminalMessageAsync(new { type = "focus" });
        }

        private void DetachButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TerminalWindow(_projectPath ?? string.Empty);
            WindowOwnerService.ShowOwned(window, this);
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
