using GitDeployPro.Services;
using GitDeployPro.Services.Terminal;
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

        public TerminalControl()
        {
            InitializeComponent();
            _configService = new ConfigurationService();

            Loaded += TerminalControl_Loaded;
            Unloaded += TerminalControl_Unloaded;
        }

        public void SetProjectPath(string path)
        {
            _projectPath = path;
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
                await fallbackSession.DisposeAsync();
                await HandleConnectionFailureAsync(ex, "Failed to start local terminal");
            }
        }

        public async Task ConnectAsync(string host, string user, string password, int port)
        {
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
            lock (_activeTerminals)
            {
                _activeTerminals.Add(this);
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
            lock (_activeTerminals)
            {
                _activeTerminals.Remove(this);
            }

            await DisconnectAsync(includeCloseMessage: false);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                await DisconnectAsync();
                return;
            }

            await ConnectAsync();
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
            ConnectButton.Content = "❌ Disconnect";
            ConnectButton.Background = System.Windows.Media.Brushes.DarkRed;
            ConnectButton.IsEnabled = true;
        }

        private void SetDisconnectedStatus()
        {
            StatusText.Text = "Disconnected";
            StatusIndicator.Background = System.Windows.Media.Brushes.Gray;
            ConnectButton.Content = "🔌 Connect";
            ConnectButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204));
            ConnectButton.IsEnabled = true;
        }

        private async Task InjectCommandTextAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (_session == null || !_isConnected)
            {
                await WriteToTerminalAsync($"\r\n> {command}\r\n");
                await FocusTerminalAsync();
                return;
            }

            await _session.WriteAsync(command);
            await FocusTerminalAsync();
        }

        public Task FocusTerminalAsync()
        {
            return PostTerminalMessageAsync(new { type = "focus" });
        }

        private void DetachButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TerminalWindow(_projectPath ?? string.Empty);
            window.Show();
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
