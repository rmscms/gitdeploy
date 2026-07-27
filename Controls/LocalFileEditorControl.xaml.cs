using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Services;
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

        public LocalFileEditorControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            CodeEditor.TextChanged += (_, _) =>
            {
                if (_suppressTextChanged || string.IsNullOrEmpty(_filePath) || !_usingSimpleEditor)
                {
                    return;
                }

                SetDirty(!string.Equals(CodeEditor.Text, _originalContent, StringComparison.Ordinal));
            };
        }

        public bool IsOpen => Visibility == Visibility.Visible && !string.IsNullOrEmpty(_filePath);

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

            RememberHome();
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
            if (!IsOpen)
            {
                Visibility = Visibility.Collapsed;
                EditorModeChanged?.Invoke(this, new LocalEditorModeChangedEventArgs(false, string.Empty));
                return true;
            }

            if (!force && _isDirty)
            {
                var result = ModernMessageBox.ShowWithResult(
                    "Save changes before closing?",
                    "Local editor",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    return false;
                }

                if (result == MessageBoxResult.Yes)
                {
                    if (!_usingSimpleEditor)
                    {
                        try
                        {
                            var monacoText = GetMonacoContentAsync().GetAwaiter().GetResult();
                            _suppressTextChanged = true;
                            CodeEditor.Text = monacoText;
                            _suppressTextChanged = false;
                            _usingSimpleEditor = true;
                        }
                        catch
                        {
                            // fall through to simple buffer
                        }
                    }

                    if (!TrySave(out var saveError))
                    {
                        ModernMessageBox.Show($"Save failed:\n{saveError}", "Local editor", MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                }
            }

            _filePath = string.Empty;
            _originalContent = string.Empty;
            _suppressTextChanged = true;
            CodeEditor.Text = string.Empty;
            _suppressTextChanged = false;
            SetDirty(false);
            PathText.Text = "No file open";
            StatusText.Text = "Closed.";
            Visibility = Visibility.Collapsed;
            RestoreHome();
            EditorModeChanged?.Invoke(this, new LocalEditorModeChangedEventArgs(false, string.Empty));
            return true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => TryClose();

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

        private bool TrySave(out string error)
        {
            error = string.Empty;
            try
            {
                var content = _usingSimpleEditor
                    ? (CodeEditor.Text ?? string.Empty)
                    : GetMonacoContentSyncFallback();

                File.WriteAllText(_filePath, content);
                _originalContent = content;
                if (_usingSimpleEditor)
                {
                    _suppressTextChanged = true;
                    CodeEditor.Text = content;
                    _suppressTextChanged = false;
                }

                SetDirty(false);
                StatusText.Text = $"Saved {AppTimeService.LocalNow:HH:mm:ss}";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private async Task<bool> TrySaveAsync()
        {
            try
            {
                var content = _usingSimpleEditor
                    ? (CodeEditor.Text ?? string.Empty)
                    : await GetMonacoContentAsync();

                await File.WriteAllTextAsync(_filePath, content);
                _originalContent = content;
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

        private string GetMonacoContentSyncFallback()
        {
            // Best-effort sync path for TryClose; prefer async save when possible.
            return CodeEditor.Text ?? string.Empty;
        }

        private void SetDirty(bool dirty)
        {
            _isDirty = dirty;
            SaveButton.IsEnabled = dirty;
            RevertButton.IsEnabled = dirty;
            SaveButton.Content = dirty ? "Save *" : "Save";
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
            if (EditorWebView.CoreWebView2 == null)
            {
                return CodeEditor.Text ?? string.Empty;
            }

            var scriptResult = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.__getValue && window.__getValue()");
            return string.IsNullOrWhiteSpace(scriptResult)
                ? string.Empty
                : JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
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
