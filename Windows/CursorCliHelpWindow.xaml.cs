using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Windows
{
    public partial class CursorCliHelpWindow : Window
    {
        private const string DocsUrl = "https://cursor.com/docs/cli/overview";

        private readonly LocalizationService _loc = LocalizationService.Instance;
        private readonly List<(string TitleKey, string BodyKey, string? CmdKey)> _sections =
        [
            ("cursor.help.s1.title", "cursor.help.s1.body", "cursor.help.s1.cmd"),
            ("cursor.help.s2.title", "cursor.help.s2.body", "cursor.help.s2.cmd"),
            ("cursor.help.s3.title", "cursor.help.s3.body", "cursor.help.s3.cmd"),
            ("cursor.help.s4.title", "cursor.help.s4.body", null),
            ("cursor.help.s5.title", "cursor.help.s5.body", null),
            ("cursor.help.s6.title", "cursor.help.s6.body", "cursor.help.s6.cmd")
        ];

        private string _helpLanguage = LocalizationService.English;
        private bool _suppressLangChange;

        public CursorCliHelpWindow()
        {
            InitializeComponent();
            _helpLanguage = _loc.Language;

            HelpLangComboBox.ItemsSource = _loc.GetLanguageOptions();
            _suppressLangChange = true;
            HelpLangComboBox.SelectedValue = _helpLanguage;
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
            Title = T("cursor.help.title");
            TitleText.Text = T("cursor.help.title");
            SubtitleText.Text = T("cursor.help.subtitle");
            LangLabelText.Text = T("cursor.help.langLabel");
            LoginButton.Content = T("cursor.help.login");
            TestButton.Content = T("cursor.help.test");
            DocsButton.Content = T("cursor.help.docs");
            CloseButton.Content = T("common.close");

            if (string.IsNullOrWhiteSpace(TestResultText.Text)
                || TestResultText.Text == T("cursor.help.testHint")
                || TestResultText.Text.Contains("Login", StringComparison.OrdinalIgnoreCase)
                || TestResultText.Text.Contains("لاگین", StringComparison.Ordinal))
            {
                TestResultText.Text = T("cursor.help.testHint");
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
            }

            RebuildSections();
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
                LineHeight = 18
            };
            SectionsPanel.Children.Add(body);

            if (string.IsNullOrWhiteSpace(cmdKey))
            {
                return;
            }

            var cmd = T(cmdKey);
            if (string.IsNullOrWhiteSpace(cmd) || cmd == cmdKey)
            {
                return;
            }

            var lineCount = Math.Max(1, cmd.Split('\n').Length);
            var isLoginCmd = string.Equals(cmdKey, "cursor.help.s2.cmd", StringComparison.Ordinal);
            var box = new System.Windows.Controls.TextBox
            {
                Text = cmd,
                IsReadOnly = true,
                BorderThickness = new Thickness(1),
                BorderBrush = (System.Windows.Media.Brush)FindResource(
                    isLoginCmd ? "Accent.Primary" : "Border.Subtle"),
                Background = (System.Windows.Media.Brush)FindResource("Surface.Raised"),
                Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = isLoginCmd ? 13.5 : 11,
                Padding = isLoginCmd ? new Thickness(14, 14, 14, 14) : new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = isLoginCmd ? Math.Max(96, 28 + lineCount * 28) : (lineCount > 1 ? 72 : 36),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            SectionsPanel.Children.Add(box);
        }

        private void HelpLangComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLangChange || HelpLangComboBox.SelectedItem is not LocalizationService.LanguageOption option)
            {
                return;
            }

            _helpLanguage = option.Code;
            ApplyHelpLanguage();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CursorAgentBridge.Instance.OpenLoginTerminal();
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Info");
                TestResultText.Text = T("cursor.help.loginOpened");
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
            LoginButton.IsEnabled = false;
            TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
            TestResultText.Text = T("cursor.help.testing");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var result = await CursorAgentBridge.Instance
                    .TestConnectionAsync(cts.Token)
                    .ConfigureAwait(true);

                if (result.NeedsLogin)
                {
                    TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Warning");
                    TestResultText.Text = result.Message;
                    try
                    {
                        CursorAgentBridge.Instance.OpenLoginTerminal(result.AgentPath);
                        TestResultText.Text = result.Message + Environment.NewLine + T("cursor.help.loginOpened");
                    }
                    catch
                    {
                        // Message already explains login is required.
                    }

                    return;
                }

                if (result.Ok)
                {
                    TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Success");
                    var accountLine = string.IsNullOrWhiteSpace(result.Account)
                        ? string.Empty
                        : Environment.NewLine + T("cursor.help.testLoggedInAs", result.Account);
                    var snippet = string.IsNullOrWhiteSpace(result.OutputSnippet)
                        ? string.Empty
                        : Environment.NewLine + result.OutputSnippet;
                    TestResultText.Text = T("cursor.help.testOkDetail", result.AgentPath, result.Workspace)
                                            + accountLine
                                            + snippet;
                }
                else
                {
                    TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    TestResultText.Text = result.Message;
                }
            }
            catch (OperationCanceledException)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = T("cursor.help.testTimeout");
            }
            catch (Exception ex)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                TestResultText.Text = T("cursor.help.testFail", ex.Message);
            }
            finally
            {
                TestButton.IsEnabled = true;
                LoginButton.IsEnabled = true;
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
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
