using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Windows
{
    /// <summary>Modal with Claude Code CLI login + connection test (same idea as <see cref="CursorCliHelpWindow"/>).</summary>
    public partial class ClaudeCliHelpWindow : Window
    {
        private const string DocsUrl = "https://docs.claude.com/en/docs/claude-code/overview";

        private readonly string? _configuredPath;

        public ClaudeCliHelpWindow(string? configuredPath = null)
        {
            InitializeComponent();
            _configuredPath = configuredPath;

            Title = Loc.T("claude.help.title");
            TitleText.Text = Loc.T("claude.help.title");
            SubtitleText.Text = Loc.T("claude.help.subtitle");
            StepsText.Text = Loc.T("claude.help.steps");
            LoginButton.Content = Loc.T("claude.help.login");
            TestButton.Content = Loc.T("claude.help.test");
            DocsButton.Content = Loc.T("claude.help.docs");
            CloseButton.Content = Loc.T("common.close");
            SetResult(Loc.T("claude.help.testHint"), "Text.Muted");
        }

        private void SetResult(string text, string brushKey)
        {
            TestResultText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
            TestResultText.Text = text;
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClaudeAgentBridge.Instance.InvalidatePathCache();
                ClaudeAgentBridge.Instance.OpenLoginTerminal(_configuredPath);
                SetResult(Loc.T("claude.loginOpened"), "Status.Info");
            }
            catch (Exception ex)
            {
                SetResult(ex.Message, "Status.Error");
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            LoginButton.IsEnabled = false;
            SetResult(Loc.T("claude.help.testing"), "Text.Muted");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var result = await ClaudeAgentBridge.Instance
                    .TestConnectionAsync(_configuredPath, cts.Token)
                    .ConfigureAwait(true);

                if (result.NeedsLogin)
                {
                    SetResult(result.Message + Environment.NewLine + Loc.T("claude.help.pressLogin"), "Status.Warning");
                }
                else
                {
                    SetResult(result.Message, result.Ok ? "Status.Success" : "Status.Error");
                }
            }
            catch (OperationCanceledException)
            {
                SetResult(Loc.T("claude.testFail", "timeout"), "Status.Error");
            }
            catch (Exception ex)
            {
                SetResult(Loc.T("claude.testFail", ex.Message), "Status.Error");
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
                Process.Start(new ProcessStartInfo { FileName = DocsUrl, UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
