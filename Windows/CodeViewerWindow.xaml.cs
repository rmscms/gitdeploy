using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using GitDeployPro.Controls;
using Microsoft.Web.WebView2.Core;

namespace GitDeployPro.Windows
{
    public partial class CodeViewerWindow : Window
    {
        private readonly string _displayPath;
        private string _content;
        private readonly string _absolutePath;
        private static string? _htmlTemplate;
        private bool _isEditMode;
        private readonly bool _canSave;
        private bool _hasUnsavedChanges;
        private bool _isSaving;

        public CodeViewerWindow(string displayPath, string content, string absolutePath)
        {
            InitializeComponent();
            _displayPath = displayPath;
            _content = content ?? string.Empty;
            _absolutePath = absolutePath ?? string.Empty;
            _canSave = !string.IsNullOrWhiteSpace(_absolutePath) && File.Exists(_absolutePath);

            TitleText.Text = Path.GetFileName(displayPath);
            SubTitleText.Text = displayPath;
            SaveButton.IsEnabled = _canSave;
            UpdateSaveButtonState();

            Loaded += CodeViewerWindow_Loaded;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_hasUnsavedChanges && !_isSaving)
            {
                var result = ModernMessageBox.ShowWithResult(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    primaryText: "Save",
                    secondaryText: "Don't Save",
                    cancelText: "Cancel");

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (result == MessageBoxResult.Yes)
                {
                    var saved = SaveContentAsync().GetAwaiter().GetResult();
                    if (!saved)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
            base.OnClosing(e);
        }

        private async void CodeViewerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await EnsureHtmlTemplateAsync();
                await CodeWebView.EnsureCoreWebView2Async();

                CodeWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                CodeWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                CodeWebView.CoreWebView2.NavigateToString(_htmlTemplate ?? string.Empty);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Unable to initialize code viewer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _ = SendPayloadAsync();
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson);
                if (message?.Type == "ready")
                {
                    _ = SendPayloadAsync();
                }
                else if (message?.Type == "dirty" && message.Value.HasValue)
                {
                    Dispatcher.Invoke(() => SetDirtyState(message.Value.Value));
                }
            }
            catch
            {
                // ignore malformed messages
            }
        }

        private async Task SendPayloadAsync()
        {
            if (CodeWebView?.CoreWebView2 == null) return;
            var payload = JsonSerializer.Serialize(new
            {
                type = "load",
                filePath = _displayPath,
                content = _content
            });

            try
            {
                await CodeWebView.CoreWebView2.ExecuteScriptAsync($"window.__loadCode && window.__loadCode({payload});");
            }
            catch
            {
                try
                {
                    CodeWebView.CoreWebView2.PostWebMessageAsJson(payload);
                }
                catch { }
            }
        }

        private static async Task EnsureHtmlTemplateAsync()
        {
            if (!string.IsNullOrEmpty(_htmlTemplate)) return;
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Resources", "CodeViewer.html");
            if (File.Exists(htmlPath))
            {
                using var reader = File.OpenText(htmlPath);
                _htmlTemplate = await reader.ReadToEndAsync();
                return;
            }

            var assembly = typeof(CodeViewerWindow).Assembly;
            var resourceName = "GitDeployPro.Resources.CodeViewer.html";
            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                throw new FileNotFoundException("Code viewer HTML template not found in embedded resources.", htmlPath);
            }

            using var fallbackReader = new StreamReader(resourceStream);
            _htmlTemplate = await fallbackReader.ReadToEndAsync();
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_content);
                await Task.CompletedTask;
            }
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void EditToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isEditMode = !_isEditMode;
            EditToggleButton.Content = _isEditMode ? "Disable Edit" : "Enable Edit";

            try
            {
                var script = $"window.__setEditable && window.__setEditable({_isEditMode.ToString().ToLowerInvariant()});";
                await CodeWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveContentAsync();
        }

        private async Task<bool> SaveContentAsync()
        {
            if (!_canSave)
            {
                System.Windows.MessageBox.Show("Cannot save because original file path is not available.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                _isSaving = true;
                var scriptResult = await CodeWebView.CoreWebView2.ExecuteScriptAsync("window.__getValue && window.__getValue()");
                var text = string.IsNullOrWhiteSpace(scriptResult) ? string.Empty : JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
                File.WriteAllText(_absolutePath, text);
                _content = text;
                SetDirtyState(false);
                await CodeWebView.CoreWebView2.ExecuteScriptAsync("window.__markClean && window.__markClean();");
                System.Windows.MessageBox.Show("File saved successfully.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Unable to save file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _isSaving = false;
            }
        }

        private void SetDirtyState(bool dirty)
        {
            _hasUnsavedChanges = dirty;
            UpdateSaveButtonState();
        }

        private void UpdateSaveButtonState()
        {
            var brush = CreateBrush(_hasUnsavedChanges ? "#1E88E5" : "#455A64");
            SaveButton.Background = brush;
            SaveButton.BorderBrush = brush;
        }

        private static SolidColorBrush CreateBrush(string hex)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 90, 100));
            }
        }

        private sealed class WebMessage
        {
            public string? Type { get; set; }
            public bool? Value { get; set; }
        }
    }
}

