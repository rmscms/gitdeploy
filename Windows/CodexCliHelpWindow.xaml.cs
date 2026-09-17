using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Windows
{
    public partial class CodexCliHelpWindow : Window
    {
        private const string DocsUrl = "https://openrouter.ai/docs/cookbook/coding-agents/codex-cli";
        private const string KeysUrl = "https://openrouter.ai/keys";
        private const string OllamaUrl = "https://ollama.com";

        private readonly LocalizationService _loc = LocalizationService.Instance;
        private readonly List<(string TitleKey, string BodyKey, string? CmdKey)> _sections =
        [
            ("codex.help.s1.title", "codex.help.s1.body", "codex.help.s1.cmd"),
            ("codex.help.s2.title", "codex.help.s2.body", "codex.help.s2.cmd"),
            ("codex.help.s3.title", "codex.help.s3.body", "codex.help.s3.cmd"),
            ("codex.help.s4.title", "codex.help.s4.body", null),
            ("codex.help.s5.title", "codex.help.s5.body", null),
            ("codex.help.s6.title", "codex.help.s6.body", "codex.help.s6.cmd")
        ];

        private string _helpLanguage = LocalizationService.English;
        private bool _suppressLangChange;

        public CodexCliHelpWindow()
        {
            InitializeComponent();
            _helpLanguage = _loc.Language;

            HelpLangComboBox.ItemsSource = _loc.GetLanguageOptions();
            _suppressLangChange = true;
            foreach (LocalizationService.LanguageOption option in HelpLangComboBox.Items)
            {
                if (string.Equals(option.Code, _helpLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    HelpLangComboBox.SelectedItem = option;
                    break;
                }
            }

            _suppressLangChange = false;
            ApplyHelpLanguage();
        }

        private string T(string key, params object[] args) =>
            _loc.GetForLanguage(_helpLanguage, key, args);

        private void ApplyHelpLanguage()
        {
            Title = T("codex.help.title");
            TitleText.Text = T("codex.help.title");
            SubtitleText.Text = T("codex.help.subtitle");
            LangLabelText.Text = T("codex.help.langLabel");
            InstallButton.Content = T("codex.help.install");
            UpdateButton.Content = T("codex.help.update");
            KeysButton.Content = T("codex.help.keys");
            TestButton.Content = T("codex.help.test");
            DocsButton.Content = T("codex.help.docs");
            CloseButton.Content = T("common.close");

            if (string.IsNullOrWhiteSpace(TestResultText.Text)
                || TestResultText.Text == T("codex.help.testHint")
                || IsPlaceholderResult(TestResultText.Text))
            {
                TestResultText.Text = T("codex.help.testHint");
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
            }

            RebuildSections();
        }

        private bool IsPlaceholderResult(string text)
        {
            return text.Contains("Detect", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Test connection", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("تست", StringComparison.Ordinal)
                   || text.Contains("کلید", StringComparison.Ordinal);
        }

        private void RebuildSections()
        {
            SectionsPanel.Children.Clear();
            foreach (var section in _sections)
            {
                AddSection(section.TitleKey, section.BodyKey, section.CmdKey);
            }
        }

        private void AddSection(string titleKey, string bodyKey, string? cmdKey)
        {
            var title = new TextBlock
            {
                Text = T(titleKey),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary"),
                Margin = new Thickness(0, SectionsPanel.Children.Count == 0 ? 0 : 16, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            SectionsPanel.Children.Add(title);

            var body = new TextBlock
            {
                Text = T(bodyKey),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("Text.Secondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            SectionsPanel.Children.Add(body);

            if (string.IsNullOrWhiteSpace(cmdKey))
            {
                return;
            }

            var cmd = new System.Windows.Controls.TextBox
            {
                Text = T(cmdKey),
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 0),
                Background = (System.Windows.Media.Brush)FindResource("Surface.Raised"),
                Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("Border.Subtle"),
                TextWrapping = TextWrapping.Wrap
            };
            SectionsPanel.Children.Add(cmd);
        }

        private void HelpLangComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLangChange)
            {
                return;
            }

            if (HelpLangComboBox.SelectedItem is LocalizationService.LanguageOption opt
                && !string.IsNullOrWhiteSpace(opt.Code))
            {
                _helpLanguage = opt.Code;
                ApplyHelpLanguage();
            }
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CodexInstallHelper.OpenInstallTerminal();
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Info");
                TestResultText.Text = T("codex.help.installOpened");
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = ex.Message;
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButton.IsEnabled = false;
            try
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                TestResultText.Text = T("codex.updateRunning");

                var result = await CodexCliUpdateService.RunManualUpdateAsync().ConfigureAwait(true);
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource(
                    result.Ok ? "Status.Success" : "Status.Error");
                TestResultText.Text = result.Message;
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = T("codex.updateFailed", ex.Message);
            }
            finally
            {
                UpdateButton.IsEnabled = true;
            }
        }

        private void KeysButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var provider = CodexProviderCatalog.Normalize(
                    new ConfigurationService().LoadGlobalConfig().CodexProvider);
                var url = provider == CodexProviderCatalog.Ollama
                    ? OllamaUrl
                    : provider == CodexProviderCatalog.OpenAi
                        ? "https://platform.openai.com/api-keys"
                        : KeysUrl;
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Info");
                TestResultText.Text = T("codex.help.keysOpened");
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = ex.Message;
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            try
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                TestResultText.Text = T("codex.help.testRunning");

                var result = await CodexAgentBridge.Instance
                    .TestConnectionAsync(null, default)
                    .ConfigureAwait(true);

                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource(
                    result.Ok ? "Status.Success" : "Status.Error");
                TestResultText.Text = result.Message;
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = T("codex.help.testFail", ex.Message);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private void DocsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DocsUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = ex.Message;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
