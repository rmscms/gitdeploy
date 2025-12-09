using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Linq;
using Renci.SshNet;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Controls
{
    public partial class TerminalControl : System.Windows.Controls.UserControl
    {
        private SshClient? _sshClient;
        private ShellStream? _shellStream;
        
        private Process? _localProcess;
        private bool _isLocal = false;

        private ConfigurationService _configService;
        private bool _isConnected = false;
        private string? _projectPath;
        private bool _typingEnabled = true; // Type toggle state
        private static readonly HashSet<TerminalControl> _activeTerminals = new HashSet<TerminalControl>();

        public TerminalControl()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            Loaded += TerminalControl_Loaded;
            Unloaded += TerminalControl_Unloaded;
            
            // Header: keep explicit colors
            AppendText("GitDeploy Pro Terminal [v1.0]\n", System.Windows.Media.Brushes.Cyan);
            // Info: Use null to inherit default theme
            AppendText("Ready to connect...\n\n", null);
        }

        private void TerminalControl_Loaded(object sender, RoutedEventArgs e)
        {
            lock (_activeTerminals)
            {
                _activeTerminals.Add(this);
            }
        }

        private void TerminalControl_Unloaded(object sender, RoutedEventArgs e)
        {
            lock (_activeTerminals)
            {
                _activeTerminals.Remove(this);
            }
        }

        public void SetProjectPath(string path)
        {
            _projectPath = path;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                Disconnect();
                return;
            }

            await ConnectAsync();
        }

        public async Task ConnectLocal(string shell = "cmd.exe")
        {
             if (_isConnected) Disconnect();

             StatusText.Text = "Starting Local Terminal...";
             StatusIndicator.Background = System.Windows.Media.Brushes.Orange;
             ConnectButton.IsEnabled = false;
             
             try 
             {
                 _localProcess = new Process();
                 _localProcess.StartInfo.FileName = shell;
                 _localProcess.StartInfo.UseShellExecute = false;
                 _localProcess.StartInfo.RedirectStandardInput = true;
                 _localProcess.StartInfo.RedirectStandardOutput = true;
                 _localProcess.StartInfo.RedirectStandardError = true;
                 _localProcess.StartInfo.CreateNoWindow = true;
                 _localProcess.StartInfo.WorkingDirectory = _projectPath ?? "C:\\";
                 
                 // Needed for some encoding handling
                 _localProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                 _localProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                 // _localProcess.StartInfo.StandardInputEncoding = Encoding.UTF8; // Optional, might help

                 _localProcess.Start();
                 _localProcess.StandardInput.AutoFlush = true; // IMPORTANT: Ensure input is sent immediately
                 
                 _isLocal = true;
                 _isConnected = true;

                 // Start readers
                 _ = ReadLocalStreamAsync(_localProcess.StandardOutput);
                 _ = ReadLocalStreamAsync(_localProcess.StandardError);

                 StatusText.Text = $"Local ({shell})";
                 StatusIndicator.Background = System.Windows.Media.Brushes.LimeGreen;
                 ConnectButton.Content = "❌ Disconnect";
                 ConnectButton.Background = System.Windows.Media.Brushes.DarkRed;
                 ConnectButton.IsEnabled = true;
                 TerminalOutput.Focus();
             }
             catch (Exception ex)
             {
                 AppendText($"Failed to start local terminal: {ex.Message}\n", System.Windows.Media.Brushes.Red);
                 Disconnect();
             }
        }

        public async Task ConnectAsync(string host, string user, string password, int port)
        {
             try
             {
                if (_isConnected) Disconnect();

                StatusText.Text = "Connecting...";
                StatusIndicator.Background = System.Windows.Media.Brushes.Orange;
                ConnectButton.IsEnabled = false;

                await Task.Run(() =>
                {
                    try
                    {
                        var connectionInfo = new ConnectionInfo(
                            host,
                            port == 21 ? 22 : port,
                            user,
                            new PasswordAuthenticationMethod(user, password)
                        );

                        _sshClient = new SshClient(connectionInfo);
                        _sshClient.Connect();

                        _shellStream = _sshClient.CreateShellStream("xterm", 80, 24, 800, 600, 1024);
                        
                        _isConnected = true;
                        _isLocal = false;

                        _ = ReadStreamAsync();
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendText($"Connection Failed: {ex.Message}\n", System.Windows.Media.Brushes.Red);
                            StatusText.Text = "Failed";
                            StatusIndicator.Background = System.Windows.Media.Brushes.Red;
                            ConnectButton.IsEnabled = true;
                        });
                    }
                });

                if (_isConnected)
                {
                    StatusText.Text = $"Connected to {host}";
                    StatusIndicator.Background = System.Windows.Media.Brushes.LimeGreen;
                    ConnectButton.Content = "❌ Disconnect";
                    ConnectButton.Background = System.Windows.Media.Brushes.DarkRed;
                    ConnectButton.IsEnabled = true;
                    TerminalOutput.Focus();
                }
             }
             catch (Exception ex)
             {
                 AppendText($"Error: {ex.Message}\n", System.Windows.Media.Brushes.Red);
             }
        }

        private async Task ConnectAsync()
        {
            try
            {
                var config = _configService.LoadProjectConfig(_projectPath);
                if (string.IsNullOrEmpty(config.FtpHost) || !config.UseSSH)
                {
                    AppendText("Error: SSH is not configured for this project. Please check Settings.\n", System.Windows.Media.Brushes.Red);
                    return;
                }
                
                await ConnectAsync(config.FtpHost, config.FtpUsername, EncryptionService.Decrypt(config.FtpPassword), config.FtpPort);
            }
            catch (Exception ex)
            {
                AppendText($"Error: {ex.Message}\n", System.Windows.Media.Brushes.Red);
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_isLocal && _localProcess != null)
                {
                     try { _localProcess.Kill(); _localProcess.Dispose(); } catch {}
                     _localProcess = null;
                }
                else
                {
                    _shellStream?.Close();
                    _sshClient?.Disconnect();
                    _sshClient?.Dispose();
                }
            }
            catch { }
            finally
            {
                _isConnected = false;
                _isLocal = false;
                StatusText.Text = "Disconnected";
                StatusIndicator.Background = System.Windows.Media.Brushes.Gray;
                ConnectButton.Content = "🔌 Connect";
                ConnectButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204));
                AppendText("\nSession closed.\n", null);
            }
        }

        public void InjectCommandText(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (!_isConnected)
            {
                AppendText($"{command}\n", System.Windows.Media.Brushes.Gray);
                TerminalScroller.ScrollToBottom();
                return;
            }

            if (_isLocal && _localProcess != null)
            {
                AppendText(command, null);
                TerminalScroller.ScrollToBottom();
                _localProcess.StandardInput.Write(command);
            }
            else if (_shellStream != null)
            {
                _shellStream.Write(command);
            }
        }

        public static void BroadcastCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

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

        private async Task ReadLocalStreamAsync(StreamReader reader)
        {
            char[] buffer = new char[1024];
            while (_isConnected && _localProcess != null && !_localProcess.HasExited)
            {
                try
                {
                    // Peek or read async
                    int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        string text = new string(buffer, 0, read);
                        await Dispatcher.InvokeAsync(() => ProcessOutputSafe(text));
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
                catch { break; }
            }
        }

        private async Task ReadStreamAsync()
        {
            while (_isConnected && _shellStream != null)
            {
                try
                {
                    if (_shellStream.DataAvailable)
                    {
                        string text = _shellStream.Read();
                        if (!string.IsNullOrEmpty(text))
                        {
                            await Dispatcher.InvokeAsync(() => ProcessOutputSafe(text), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    else
                    {
                        await Task.Delay(50);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private void TerminalOutput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_isConnected) return;
            if (!_isLocal && _shellStream == null) return;

            // Handle Ctrl+C
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_isLocal && _localProcess != null)
                {
                    // Writing \x03 to StandardInput is the best attempt for redirected process
                    _localProcess.StandardInput.Write("\x03");
                }
                else if (!_isLocal)
                {
                    _shellStream?.Write("\x03");
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (_isLocal) 
                {
                     AppendText("\r\n", null); // Echo newline
                     TerminalScroller.ScrollToBottom();
                     _localProcess?.StandardInput.WriteLine();
                }
                else 
                {
                    _shellStream?.Write("\r"); 
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                if (!_isLocal) _shellStream?.Write("\b");
                else
                {
                     RemoveLastChar();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                if (_isLocal) _localProcess?.StandardInput.Write("\t");
                else _shellStream?.Write("\t");
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                 if (!_isLocal) _shellStream?.Write("\x1b[A");
                 e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (!_isLocal) _shellStream?.Write("\x1b[B");
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                if (!_isLocal) _shellStream?.Write("\x1b[D");
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                if (!_isLocal) _shellStream?.Write("\x1b[C");
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                if (_isLocal)
                {
                    AppendText(" ", null);
                    TerminalScroller.ScrollToBottom();
                    _localProcess?.StandardInput.Write(" ");
                }
                else
                {
                    _shellStream?.Write(" ");
                }
                e.Handled = true;
            }
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            if (_isConnected && _typingEnabled)
            {
                if (_isLocal && _localProcess != null)
                {
                    AppendText(e.Text, null); 
                    TerminalScroller.ScrollToBottom();
                    _localProcess.StandardInput.Write(e.Text);
                }
                else if (_shellStream != null)
                {
                    _shellStream.Write(e.Text);
                }
                e.Handled = true;
            }
            else if (!_typingEnabled)
            {
                // Typing is disabled - show visual feedback
                e.Handled = true;
            }
            base.OnPreviewTextInput(e);
        }

        // Routed from XAML to ensure caret stays visible and input is intercepted
        private void TerminalOutput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            OnPreviewTextInput(e);
        }

        private void TypeToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            _typingEnabled = true;
            if (TypeToggleButton != null)
            {
                TypeToggleButton.ToolTip = "Type (Enabled)";
            }
        }

        private void TypeToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            _typingEnabled = false;
            if (TypeToggleButton != null)
            {
                TypeToggleButton.ToolTip = "Type (Disabled)";
            }
        }

        private void ProcessOutputSafe(string text)
        {
            TerminalOutput.BeginChange();
            try 
            {
                ProcessOutput(text);
            }
            finally
            {
                TerminalOutput.EndChange();
                TerminalScroller.ScrollToBottom();
                TerminalOutput.CaretBrush = System.Windows.Media.Brushes.Lime;
                TerminalOutput.CaretPosition = TerminalOutput.Document.ContentEnd;
            }
        }

        private void ProcessOutput(string text)
        {
            // 1. Strip known noise
            text = text.Replace("\x1b[?2004h", "").Replace("\x1b[?2004l", "").Replace("\x1b[K", "");

            // 2. Split by ANSI Color Codes
            string pattern = @"\x1b\[([0-9;]*)m";
            var parts = Regex.Split(text, pattern);

            // Default starts as null (inherit from theme)
            System.Windows.Media.Brush? currentColor = null;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];

                if (i % 2 == 0) 
                {
                    if (!string.IsNullOrEmpty(part))
                    {
                        part = part.Replace("\r", ""); 
                        HandleTextWithControls(part, currentColor);
                    }
                }
                else 
                {
                    currentColor = ParseAnsiColor(part, currentColor);
                }
            }
        }

        private void HandleTextWithControls(string text, System.Windows.Media.Brush? color)
        {
            StringBuilder sb = new StringBuilder();
            
            foreach (char c in text)
            {
                if (c == '\b')
                {
                    if (sb.Length > 0)
                    {
                        AppendText(sb.ToString(), color);
                        sb.Clear();
                    }
                    RemoveLastChar();
                }
                else 
                {
                    sb.Append(c);
                }
            }
            
            if (sb.Length > 0)
            {
                AppendText(sb.ToString(), color);
            }

            // Ensure caret visible and focused after output
            TerminalOutput.CaretBrush = System.Windows.Media.Brushes.Lime;
            TerminalOutput.CaretPosition = TerminalOutput.Document.ContentEnd;
            TerminalOutput.Focus();
        }

        private void RemoveLastChar()
        {
            // Find the last Run in the last Paragraph
            if (TerminalOutput.Document.Blocks.LastBlock is Paragraph lastP)
            {
                if (lastP.Inlines.LastInline is Run lastRun)
                {
                    if (lastRun.Text.Length > 0)
                    {
                        lastRun.Text = lastRun.Text.Substring(0, lastRun.Text.Length - 1);
                    }
                    
                    // If Run is empty, remove it
                    if (lastRun.Text.Length == 0)
                    {
                        lastP.Inlines.Remove(lastRun);
                    }
                }
            }
        }

        private System.Windows.Media.Brush? ParseAnsiColor(string code, System.Windows.Media.Brush? current)
        {
            // Reset or empty -> null (default theme)
            if (string.IsNullOrEmpty(code) || code == "0" || code == "39") return null;

            var segments = code.Split(';');
            foreach (var seg in segments)
            {
                switch (seg)
                {
                    case "0": return null; // Reset inside complex code
                    case "39": return null; // Default foreground
                    case "30": return System.Windows.Media.Brushes.Black;
                    case "31": return System.Windows.Media.Brushes.Red;
                    case "32": return System.Windows.Media.Brushes.LimeGreen;
                    case "33": return System.Windows.Media.Brushes.Yellow;
                    case "34": return System.Windows.Media.Brushes.DodgerBlue;
                    case "35": return System.Windows.Media.Brushes.Magenta;
                    case "36": return System.Windows.Media.Brushes.Cyan;
                    case "37": return System.Windows.Media.Brushes.White;
                    case "1": return System.Windows.Media.Brushes.White; // Bold often implies bright white/intense
                    case "90": return System.Windows.Media.Brushes.Gray;
                    case "91": return System.Windows.Media.Brushes.LightCoral;
                    case "92": return System.Windows.Media.Brushes.LightGreen;
                    case "93": return System.Windows.Media.Brushes.LightYellow;
                    case "94": return System.Windows.Media.Brushes.LightBlue;
                }
            }
            return current;
        }

        private void AppendText(string text, System.Windows.Media.Brush? color)
        {
            var run = new Run(text);
            if (color != null)
            {
                run.Foreground = color;
            }
            // else inherit from parent/RichTextBox
            
            Paragraph p;
            if (TerminalOutput.Document.Blocks.LastBlock is Paragraph lastP)
            {
                p = lastP;
            }
            else
            {
                p = new Paragraph();
                TerminalOutput.Document.Blocks.Add(p);
            }
            p.Inlines.Add(run);
        }

        private void DetachButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TerminalWindow(_projectPath);
            window.Show();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            TerminalOutput.Document.Blocks.Clear();
            AppendText("Terminal Cleared.\n", System.Windows.Media.Brushes.Gray);
        }

        private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TerminalOutput == null) return;
            if (FontSizeCombo.SelectedItem is ComboBoxItem item)
            {
                if (double.TryParse(item.Content.ToString(), out double size))
                {
                    TerminalOutput.FontSize = size;
                }
            }
        }

        private void TextColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TerminalOutput == null) return;
            if (TextColorCombo.SelectedItem is ComboBoxItem item && item.Tag is string colorHex)
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
                    TerminalOutput.Foreground = new SolidColorBrush(color);
                }
                catch { }
            }
        }
    }
}
