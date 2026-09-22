using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services.Localization;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    /// <summary>
    /// Floating local terminal for the active project root.
    /// Uses the same TerminalControl (full features) with a Cmder-like xterm palette override.
    /// </summary>
    public partial class ProjectLocalTerminalWindow : MetroWindow
    {
        private static ProjectLocalTerminalWindow? _openInstance;

        private readonly string _projectPath;

        /// <summary>Cmder / Windows Terminal inspired palette — only for this window.</summary>
        private static readonly IReadOnlyDictionary<string, string> PrettyPalette =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["background"] = "#0C0C0C",
                ["foreground"] = "#CCCCCC",
                ["cursor"] = "#FFFFFF",
                ["selectionBackground"] = "rgba(58,150,221,0.35)",
                ["black"] = "#0C0C0C",
                ["red"] = "#C50F1F",
                ["green"] = "#13A10E",
                ["yellow"] = "#C19C00",
                ["blue"] = "#0037DA",
                ["magenta"] = "#881798",
                ["cyan"] = "#3A96DD",
                ["white"] = "#CCCCCC",
                ["brightBlack"] = "#767676",
                ["brightRed"] = "#E74856",
                ["brightGreen"] = "#16C60C",
                ["brightYellow"] = "#F9F1A5",
                ["brightBlue"] = "#3B78FF",
                ["brightMagenta"] = "#B4009E",
                ["brightCyan"] = "#61D6D6",
                ["brightWhite"] = "#F2F2F2"
            };

        public ProjectLocalTerminalWindow(string projectPath)
        {
            InitializeComponent();
            _projectPath = Path.GetFullPath(projectPath);
            Title = Loc.T("terminal.projectLocalTitleNamed", Path.GetFileName(_projectPath.TrimEnd('\\', '/')));
            PathText.Text = _projectPath;

            LocalTerminal.SetProjectPath(_projectPath);
            LocalTerminal.SetXtermThemeOverride(PrettyPalette, hostBackgroundHex: "#0C0C0C");
            if (LocalTerminal.DetachButton != null)
            {
                LocalTerminal.DetachButton.Visibility = Visibility.Collapsed;
            }

            Owner = System.Windows.Application.Current?.MainWindow;
            Loaded += ProjectLocalTerminalWindow_Loaded;
            Closed += (_, _) =>
            {
                if (ReferenceEquals(_openInstance, this))
                {
                    _openInstance = null;
                }
            };
        }

        public static void ShowOrFocus(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                ModernMessageBox.Show(
                    Loc.T("terminal.projectLocalNoProject"),
                    Loc.T("terminal.projectLocalTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var full = Path.GetFullPath(projectPath);
            if (_openInstance != null)
            {
                var same = string.Equals(
                    Path.GetFullPath(_openInstance._projectPath),
                    full,
                    StringComparison.OrdinalIgnoreCase);
                if (same)
                {
                    if (_openInstance.WindowState == WindowState.Minimized)
                    {
                        _openInstance.WindowState = WindowState.Normal;
                    }

                    _openInstance.Activate();
                    return;
                }

                _openInstance.Close();
            }

            var win = new ProjectLocalTerminalWindow(full);
            _openInstance = win;
            win.Show();
        }

        private async void ProjectLocalTerminalWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await StartShellAsync().ConfigureAwait(true);
        }

        private async void RestartShell_Click(object sender, RoutedEventArgs e)
        {
            await StartShellAsync().ConfigureAwait(true);
        }

        private async Task StartShellAsync()
        {
            try
            {
                LocalTerminal.SetXtermThemeOverride(PrettyPalette, hostBackgroundHex: "#0C0C0C");
                // Start shell quietly — do NOT InjectCommandText (that echoed ugly prompt tags).
                var commandLine = BuildQuietShellCommandLine();
                await LocalTerminal.ConnectLocal(commandLine).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    Loc.T("terminal.projectLocalStartFail", ex.Message),
                    Loc.T("terminal.projectLocalTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    owner: this);
            }
        }

        private static string BuildQuietShellCommandLine()
        {
            var exe = ResolvePreferredShell();
            if (exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return $"\"{exe}\"";
            }

            // Quote path; -NoLogo hides banner. Prompt stays native (xterm palette still applies).
            return $"\"{exe}\" -NoLogo";
        }

        private static string ResolvePreferredShell()
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var pwsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh))
            {
                return pwsh;
            }

            var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(powershell))
            {
                return powershell;
            }

            return Path.Combine(system, "cmd.exe");
        }
    }
}
