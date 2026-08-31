using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Theme;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Web.WebView2.Core;
using WpfPanel = System.Windows.Controls.Panel;

namespace GitDeployPro.Controls
{
    public sealed class LocalEditorModeChangedEventArgs : EventArgs
    {
        public LocalEditorModeChangedEventArgs(bool isOpen, string filePath)
        {
            IsOpen = isOpen;
            FilePath = filePath ?? string.Empty;
        }

        public bool IsOpen { get; }
        public string FilePath { get; }
    }

    public partial class LocalFileEditorControl : System.Windows.Controls.UserControl
    {
        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".tar", ".gz",
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".pdf",
            ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4", ".avi", ".mov",
            ".sqlite", ".db", ".bin"
        };

        private string _filePath = string.Empty;
        private string _originalContent = string.Empty;
        private bool _isDirty;
        private bool _suppressTextChanged;
        private bool _usingSimpleEditor = true;
        private bool _monacoReady;
        private bool _webEventsBound;
        private bool _promoteInProgress;
        private string? _editorHtmlTemplate;
        private WpfPanel? _homePanel;
        private UIElementCollection? _homeChildren;

        public event EventHandler<LocalEditorModeChangedEventArgs>? EditorModeChanged;
        public event EventHandler? FloatRequested;

        private bool _isFloated;

        public LocalFileEditorControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            ThemeService.Instance.ThemeChanged += OnDeployThemeChanged;
            Unloaded += (_, _) => ThemeService.Instance.ThemeChanged -= OnDeployThemeChanged;
            ApplyEditorChromeTheme();
            CodeEditor.TextChanged += (_, _) =>
            {
                if (_suppressTextChanged || string.IsNullOrEmpty(_filePath) || !_usingSimpleEditor)
                {
                    return;
                }

                SetDirty(!string.Equals(CodeEditor.Text, _originalContent, StringComparison.Ordinal));
            };
        }

        private void OnDeployThemeChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnDeployThemeChanged(sender, e));
                return;
            }

            ApplyEditorChromeTheme();
            _ = ApplyMonacoThemeAsync();
        }

        private void ApplyEditorChromeTheme()
        {
            var tokens = ThemeService.Instance.CurrentTokens;
            var fallbackBg = tokens.GetBrush(
                "editor.fallbackBackground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
            var fallbackFg = tokens.GetBrush(
                "editor.fallbackForeground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F4F7FF"));
            if (CodeEditor != null)
            {
                CodeEditor.Background = fallbackBg;
                CodeEditor.Foreground = fallbackFg;
            }

            if (EditorWebView != null)
            {
                var webBg = tokens.GetHex("editor.webviewBackground", "#FF1E1E1E");
                try
                {
                    EditorWebView.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(
                        webBg.Length == 9 && webBg.StartsWith("#", StringComparison.Ordinal)
                            ? "#" + webBg[3..]
                            : webBg);
                }
                catch
                {
                    // ignore
                }
            }

            ApplyToolbarActionState(SaveButton, _isDirty);
            ApplyToolbarActionState(RevertButton, _isDirty);
            ApplyToolbarActionState(FloatEditorButton, _isFloated);
        }

        private async Task ApplyMonacoThemeAsync()
        {
            if (!_monacoReady || EditorWebView?.CoreWebView2 == null)
            {
                return;
            }

            var tokens = ThemeService.Instance.CurrentTokens;
            var theme = tokens.MonacoTheme.Replace("\"", "");
            var bg = tokens.GetHex("editor.webviewBackground", "#1E1E1E").Replace("\"", "");
            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__setTheme && window.__setTheme(\"{theme}\", \"{bg}\");");
        }

        public bool IsOpen => Visibility == Visibility.Visible && !string.IsNullOrEmpty(_filePath);

        public string OpenedFilePath => _filePath;

        public async Task TryReloadFromDiskIfMatchesAsync(string localPath)
        {
            if (!IsOpen || string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(_filePath))
            {
                return;
            }

            string opened;
            string uploaded;
            try
            {
                opened = Path.GetFullPath(_filePath);
                uploaded = Path.GetFullPath(localPath);
            }
            catch
            {
                return;
            }

            if (!string.Equals(opened, uploaded, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isDirty)
            {
                StatusText.Text = "Skipped reload (unsaved changes).";
                return;
            }

            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var content = await File.ReadAllTextAsync(_filePath);
                _originalContent = content;
                if (_usingSimpleEditor)
                {
                    _suppressTextChanged = true;
                    CodeEditor.Text = content;
                    _suppressTextChanged = false;
                }
                else
                {
                    await LoadMonacoContentAsync(_filePath, content);
                }

                SetDirty(false);
                StatusText.Text = "Reloaded from disk.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Reload failed: {ex.Message}";
            }
        }

        public bool TryOpenFile(string fullPath, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                error = "File not found.";
                return false;
            }

            var ext = Path.GetExtension(fullPath);
            if (BinaryExtensions.Contains(ext))
            {
                error = $"Cannot edit binary file ({ext}).";
                return false;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length > 5 * 1024 * 1024)
                {
                    error = "File is larger than 5 MB.";
                    return false;
                }

                var content = File.ReadAllText(fullPath);
                _filePath = fullPath;
                _originalContent = content;
                PathText.Text = fullPath;
                ApplyHighlighting(ext);

                // Always start with the simple editor for instant open.
                _usingSimpleEditor = true;
                EditorWebView.Visibility = Visibility.Collapsed;
                CodeEditor.Visibility = Visibility.Visible;
                _suppressTextChanged = true;
                CodeEditor.Text = content;
                _suppressTextChanged = false;
                SetDirty(false);
                StatusText.Text = "Simple editor ready. Loading Monaco…";
                Visibility = Visibility.Visible;
                EditorModeChanged?.Invoke(this, new LocalEditorModeChangedEventArgs(true, _filePath));

                _ = PromoteToMonacoAsync();
                FocusEditorSurface();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void HostIn(Decorator host)
        {
            if (host == null)
            {
                return;
            }

            if (_homePanel == null)
            {
                RememberHome();
            }

            DetachFromParent();
            host.Child = this;
            Visibility = Visibility.Visible;
            Margin = new Thickness(0);
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        public void RestoreHome()
        {
            if (_homePanel == null || _homeChildren == null)
            {
                return;
            }

            DetachFromParent();
            if (!_homeChildren.Contains(this))
            {
                _homeChildren.Add(this);
            }

            Visibility = Visibility.Collapsed;
        }

        public bool TryClose(bool force = false)
        {
            if (!force && _isDirty)
            {
                return false;
            }

            return FinishClose();
        }

        public async Task<bool> TryCloseAsync(bool force = false)
        {
            if (!IsOpen)
            {
                return FinishClose();
            }

            if (!force && _isDirty)
            {
                // Capture Monaco text before the modal. After ShowDialog, WebView2
                // ExecuteScriptAsync can hang and the owner looks frozen / click-blocked.
                var pendingContent = await GetEditorContentAsync();
                var result = ModernMessageBox.ShowWithResult(
                    "Save changes before closing?",
                    "Local editor",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    context: this);

                if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
                {
                    return false;
                }

                if (result == MessageBoxResult.Yes)
                {
                    if (!await TrySaveContentAsync(pendingContent))
                    {
                        return false;
                    }
                }
            }

            return FinishClose();
        }

        private bool FinishClose()
        {
            _filePath = string.Empty;
            _originalContent = string.Empty;
            _suppressTextChanged = true;
            CodeEditor.Text = string.Empty;
            _suppressTextChanged = false;
            SetDirty(false);
            PathText.Text = "No file open";
            StatusText.Text = "Closed.";
            Visibility = Visibility.Collapsed;
            EditorKeyboardScope.DisarmMonaco(EditorWebView);
            RestoreHome();
            EditorModeChanged?.Invoke(this, new LocalEditorModeChangedEventArgs(false, string.Empty));
            return true;
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            await TryCloseAsync();
        }

        private void FloatEditorButton_Click(object sender, RoutedEventArgs e)
        {
            FloatRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetEditorFloated(bool floated)
        {
            if (FloatEditorButton == null)
            {
                return;
            }

            _isFloated = floated;
            FloatEditorButton.Content = floated ? "⊟" : "⧉";
            FloatEditorButton.ToolTip = Loc.T(floated ? "deploy.tip.dockEditor" : "deploy.tip.floatEditor");
            ApplyToolbarActionState(FloatEditorButton, floated);
        }

        private async void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                return;
            }

            if (_usingSimpleEditor)
            {
                _suppressTextChanged = true;
                CodeEditor.Text = _originalContent;
                _suppressTextChanged = false;
            }
            else
            {
                await LoadMonacoContentAsync(_filePath, _originalContent);
            }

            SetDirty(false);
            StatusText.Text = "Reverted.";
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await TrySaveAsync())
            {
                // error already shown
            }
        }

        private async Task<bool> TrySaveAsync()
        {
            var content = await GetEditorContentAsync();
            return await TrySaveContentAsync(content);
        }

        private async Task<bool> TrySaveContentAsync(string content)
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                return false;
            }

            try
            {
                await File.WriteAllTextAsync(_filePath, content ?? string.Empty);
                _originalContent = content ?? string.Empty;
                SetDirty(false);
                StatusText.Text = $"Saved {AppTimeService.LocalNow:HH:mm:ss}";
                return true;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Save failed:\n{ex.Message}", "Local editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<string> GetEditorContentAsync()
        {
            if (_usingSimpleEditor || !_monacoReady || EditorWebView?.CoreWebView2 == null)
            {
                return CodeEditor.Text ?? string.Empty;
            }

            return await GetMonacoContentAsync();
        }

        private void SetDirty(bool dirty)
        {
            _isDirty = dirty;
            SaveButton.IsEnabled = dirty;
            RevertButton.IsEnabled = dirty;
            ApplyToolbarActionState(SaveButton, dirty);
            ApplyToolbarActionState(RevertButton, dirty);
        }

        private static void ApplyToolbarActionState(System.Windows.Controls.Button? button, bool active)
        {
            if (button == null)
            {
                return;
            }

            button.Tag = active ? "active" : null;
            if (!active)
            {
                button.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
                return;
            }

            var tokens = ThemeService.Instance.CurrentTokens;
            button.Foreground = tokens.GetBrush(
                "editor.actionActiveForeground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6B84D"));
            button.Background = tokens.GetBrush(
                "editor.actionActiveBackground",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A1408"));
            button.BorderBrush = tokens.GetBrush(
                "editor.actionActiveBorder",
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6B84D"));
        }

        private void ApplyHighlighting(string extension)
        {
            try
            {
                CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(extension);
            }
            catch
            {
                CodeEditor.SyntaxHighlighting = null;
            }
        }

        private async Task PromoteToMonacoAsync()
        {
            if (_promoteInProgress || string.IsNullOrEmpty(_filePath))
            {
                return;
            }

            _promoteInProgress = true;
            try
            {
                await EnsureMonacoHostAsync();
                var waitStart = DateTime.UtcNow;
                while (!_monacoReady && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(12))
                {
                    await Task.Delay(120);
                }

                if (!_monacoReady || EditorWebView.CoreWebView2 == null)
                {
                    StatusText.Text = "Simple editor active (Monaco unavailable).";
                    return;
                }

                var buffer = CodeEditor.Text ?? string.Empty;
                await LoadMonacoContentAsync(_filePath, buffer);
                await SetMonacoEditableAsync(true);

                CodeEditor.Visibility = Visibility.Collapsed;
                EditorWebView.Visibility = Visibility.Visible;
                _usingSimpleEditor = false;
                SetDirty(!string.Equals(buffer, _originalContent, StringComparison.Ordinal));
                StatusText.Text = "Monaco editor ready.";
                FocusEditorSurface();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Simple editor active ({ex.Message}).";
                _usingSimpleEditor = true;
                CodeEditor.Visibility = Visibility.Visible;
                EditorWebView.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _promoteInProgress = false;
            }
        }

        private async Task EnsureMonacoHostAsync()
        {
            await EnsureEditorHtmlTemplateAsync();
            EditorWebView.Visibility = Visibility.Visible;
            await EditorWebView.EnsureCoreWebView2Async();

            if (!_webEventsBound && EditorWebView.CoreWebView2 != null)
            {
                EditorWebView.CoreWebView2.WebMessageReceived += EditorWebView_WebMessageReceived;
                EditorWebView.CoreWebView2.NavigationCompleted += EditorWebView_NavigationCompleted;
                _webEventsBound = true;
            }

            if (!_monacoReady && EditorWebView.CoreWebView2 != null)
            {
                EditorWebView.CoreWebView2.NavigateToString(
                    _editorHtmlTemplate ?? "<html><body>Editor template missing.</body></html>");
            }
        }

        private async Task EnsureEditorHtmlTemplateAsync()
        {
            if (!string.IsNullOrEmpty(_editorHtmlTemplate))
            {
                return;
            }

            var assembly = Assembly.GetExecutingAssembly();
            await using var stream = assembly.GetManifestResourceStream("GitDeployPro.Resources.CodeViewer.html")
                ?? throw new FileNotFoundException("Code editor template not found.");
            using var reader = new StreamReader(stream);
            _editorHtmlTemplate = await reader.ReadToEndAsync();
        }

        private async void EditorWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                return;
            }

            _monacoReady = true;
            _ = ApplyMonacoThemeAsync();
            // PromoteToMonacoAsync is already waiting on readiness; avoid re-entry here.
        }

        private void EditorWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (message?.Type == "ready")
                {
                    _monacoReady = true;
                    return;
                }

                if (message?.Type == "dirty" && !_usingSimpleEditor)
                {
                    _ = UpdateDirtyFromMonacoAsync();
                }
            }
            catch
            {
                // ignore malformed messages
            }
        }

        private async Task UpdateDirtyFromMonacoAsync()
        {
            var content = await GetMonacoContentAsync();
            SetDirty(!string.Equals(content, _originalContent, StringComparison.Ordinal));
        }

        private async Task LoadMonacoContentAsync(string filePath, string content)
        {
            if (EditorWebView.CoreWebView2 == null)
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = "load",
                filePath,
                content = content ?? string.Empty
            });

            await EditorWebView.CoreWebView2.ExecuteScriptAsync($"window.__loadCode && window.__loadCode({payload});");
            await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__markClean && window.__markClean();");
            await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__focusEditor && window.__focusEditor();");
        }

        private void EditorWebView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FocusEditorSurface();
        }

        private void EditorWebView_GotFocus(object sender, RoutedEventArgs e)
        {
            FocusEditorSurface(monacoOnly: true);
        }

        private void FocusEditorSurface(bool monacoOnly = false)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_usingSimpleEditor)
                {
                    EditorKeyboardScope.ArmAvalon(CodeEditor);
                    if (!monacoOnly)
                    {
                        CodeEditor.Focus();
                        CodeEditor.TextArea?.Focus();
                    }

                    return;
                }

                if (EditorWebView != null && EditorWebView.Visibility == Visibility.Visible)
                {
                    EditorKeyboardScope.ArmMonaco(EditorWebView);
                    if (!monacoOnly)
                    {
                        EditorWebView.Focus();
                    }

                    if (EditorWebView.CoreWebView2 != null)
                    {
                        _ = EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__focusEditor && window.__focusEditor();");
                    }
                }
            }), DispatcherPriority.Input);
        }

        private async Task SetMonacoEditableAsync(bool enabled)
        {
            if (EditorWebView.CoreWebView2 == null)
            {
                return;
            }

            var flag = enabled ? "true" : "false";
            await EditorWebView.CoreWebView2.ExecuteScriptAsync($"window.__setEditable && window.__setEditable({flag});");
        }

        private async Task<string> GetMonacoContentAsync()
        {
            if (EditorWebView?.CoreWebView2 == null)
            {
                return CodeEditor.Text ?? string.Empty;
            }

            try
            {
                var scriptTask = EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__getValue && window.__getValue()");
                var completed = await Task.WhenAny(scriptTask, Task.Delay(TimeSpan.FromSeconds(2)));
                if (completed != scriptTask)
                {
                    return CodeEditor.Text ?? string.Empty;
                }

                var scriptResult = await scriptTask;
                if (string.IsNullOrWhiteSpace(scriptResult) || scriptResult == "null")
                {
                    return CodeEditor.Text ?? string.Empty;
                }

                return JsonSerializer.Deserialize<string>(scriptResult) ?? CodeEditor.Text ?? string.Empty;
            }
            catch
            {
                return CodeEditor.Text ?? string.Empty;
            }
        }

        private void RememberHome()
        {
            if (_homePanel != null)
            {
                return;
            }

            if (Parent is WpfPanel panel)
            {
                _homePanel = panel;
                _homeChildren = panel.Children;
            }
        }

        private void DetachFromParent()
        {
            switch (Parent)
            {
                case WpfPanel panel:
                    panel.Children.Remove(this);
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, this):
                    decorator.Child = null;
                    break;
                case ContentControl content when ReferenceEquals(content.Content, this):
                    content.Content = null;
                    break;
            }
        }

        private sealed class WebMessage
        {
            public string? Type { get; set; }
        }
    }
}
