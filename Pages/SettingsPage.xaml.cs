using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models; // For ProjectConfig
using GitDeployPro.Services; // For ConfigurationService, GitService, EncryptionService
using GitDeployPro.Windows; 
using System.Windows.Forms; // For FolderBrowserDialog

namespace GitDeployPro.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ConfigurationService _configService;
        private readonly GitService _gitService;
        private static readonly string[] DefaultIgnorePatterns = new[]
        {
            "bin/",
            "obj/",
            ".vs/",
            "packages/",
            "node_modules/",
            ".env",
            "*.log",
            "vendor/",
            ".gitdeploy.config",
            ".gitdeploy.history"
        };

        public SettingsPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _gitService = new GitService();
            LoadSettings();
        }

        private async void LoadSettings()
        {
            try
            {
                // 1. Load Global Config to find last project
                var globalConfig = _configService.LoadGlobalConfig();
                
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    await ReloadSettingsForPath(globalConfig.LastProjectPath);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ReloadSettingsForPath(string path)
        {
            LocalPathTextBox.Text = path;
            
            // Load Project Config
            var projectConfig = _configService.LoadProjectConfig(path);
            
            // Populate UI with Project Config (or defaults/empty if new)
            HostTextBox.Text = projectConfig.FtpHost;
            PortTextBox.Text = projectConfig.FtpPort == 0 ? "21" : projectConfig.FtpPort.ToString();
            UsernameTextBox.Text = projectConfig.FtpUsername;
            PasswordBox.Password = projectConfig.FtpPasswordDecrypted;
            RemotePathTextBox.Text = string.IsNullOrEmpty(projectConfig.RemotePath) ? "/" : projectConfig.RemotePath;
            
            UseSSHCheckBox.IsChecked = projectConfig.UseSSH;
            AutoInitGitCheckBox.IsChecked = projectConfig.AutoInitGit;
            AutoCommitCheckBox.IsChecked = projectConfig.AutoCommit;
            AutoPushCheckBox.IsChecked = projectConfig.AutoPush;
            
            var gitIgnoreLines = LoadOrCreateGitIgnoreLines(path);
            ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, gitIgnoreLines);
            
            // Set Git Service Path
            GitService.SetWorkingDirectory(path);

            // Load Branches & Remote URL
            await LoadGitInfo(projectConfig);
        }

        private async Task LoadGitInfo(ProjectConfig config)
        {
            try
            {
                if (_gitService.IsGitRepository())
                {
                    // Load Remote URL
                    string remoteUrl = await _gitService.GetRemoteUrlAsync();
                    RemoteUrlTextBox.Text = remoteUrl;

                    // Load Branches
                    var branches = await _gitService.GetBranchesAsync();
                    
                    DefaultSourceBranchComboBox.ItemsSource = branches;
                    DefaultTargetBranchComboBox.ItemsSource = branches;

                    if (branches.Contains(config.DefaultSourceBranch))
                        DefaultSourceBranchComboBox.SelectedItem = config.DefaultSourceBranch;
                    else if (branches.Any())
                        DefaultSourceBranchComboBox.SelectedIndex = 0;

                    if (branches.Contains(config.DefaultTargetBranch))
                        DefaultTargetBranchComboBox.SelectedItem = config.DefaultTargetBranch;

                    var branchStatus = await _gitService.GetBranchStatusAsync();
                    UpdateGitPushStatusBadge(branchStatus);
                }
                else
                {
                    DefaultSourceBranchComboBox.ItemsSource = null;
                    DefaultTargetBranchComboBox.ItemsSource = null;
                    RemoteUrlTextBox.Text = "";
                    UpdateGitPushStatusBadge(new BranchStatusInfo());
                }
            }
            catch { }
        }

        private void UseSSHCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (PortTextBox.Text == "21") PortTextBox.Text = "22";
        }

        private void UseSSHCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (PortTextBox.Text == "22") PortTextBox.Text = "21";
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HostTextBox.Text) || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ModernMessageBox.Show("Please enter Host and Username.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int port = 21;
            if (!int.TryParse(PortTextBox.Text, out port))
            {
                ModernMessageBox.Show("Invalid Port number.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TestConnectionButton.IsEnabled = false;
            TestConnectionButton.Content = "⏳ Testing...";

            try
            {
                using (var client = new AsyncFtpClient(HostTextBox.Text, UsernameTextBox.Text, PasswordBox.Password, port))
                {
                    await client.Connect();
                    ModernMessageBox.Show("Connection Successful! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    BrowseButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Connection Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BrowseButton.IsEnabled = false;
            }
            finally
            {
                TestConnectionButton.IsEnabled = true;
                TestConnectionButton.Content = "🔄 Test Connection";
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PortTextBox.Text, out int port))
            {
                var browser = new RemoteBrowserWindow(HostTextBox.Text, UsernameTextBox.Text, PasswordBox.Password, port);
                if (browser.ShowDialog() == true)
                {
                    RemotePathTextBox.Text = browser.SelectedPath;
                }
            }
        }

        private async void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select Local Project Folder (Git Repository)";
                dialog.ShowNewFolderButton = true;
                
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Reload everything for the new path
                    await ReloadSettingsForPath(dialog.SelectedPath);
                }
            }
        }

        private void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadGitInfo(new ProjectConfig { 
                DefaultSourceBranch = DefaultSourceBranchComboBox.SelectedItem as string ?? "",
                DefaultTargetBranch = DefaultTargetBranchComboBox.SelectedItem as string ?? ""
            });
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string projectPath = LocalPathTextBox.Text;
                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    ModernMessageBox.Show("Please select a Local Project Path.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(projectPath))
                {
                     ModernMessageBox.Show("Local Project Path does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                }

                int.TryParse(PortTextBox.Text, out int port);

                var ignoreEntries = GetIgnoreEntriesFromTextBox();

                // 1. Save Project Config
                var projectConfig = new ProjectConfig
                {
                    LocalProjectPath = projectPath,
                    FtpHost = HostTextBox.Text,
                    FtpPort = port,
                    FtpUsername = UsernameTextBox.Text,
                    FtpPassword = EncryptionService.Encrypt(PasswordBox.Password),
                    RemotePath = RemotePathTextBox.Text,
                    DefaultSourceBranch = DefaultSourceBranchComboBox.SelectedItem as string ?? "master",
                    DefaultTargetBranch = DefaultTargetBranchComboBox.SelectedItem as string ?? "",
                    
                    UseSSH = UseSSHCheckBox.IsChecked ?? false,
                    AutoInitGit = AutoInitGitCheckBox.IsChecked ?? true,
                    AutoCommit = AutoCommitCheckBox.IsChecked ?? true,
                    AutoPush = AutoPushCheckBox.IsChecked ?? false
                };
                projectConfig.ExcludePatterns = ignoreEntries.ToArray();

                _configService.SaveProjectConfig(projectConfig);

                // 2. Save Global Config (Update Last Project)
                _configService.UpdateGlobalConfig(cfg =>
                {
                    cfg.LastProjectPath = projectPath;
                });
                
                // 3. Update Git Service
                GitService.SetWorkingDirectory(projectPath);

                // 3.5. Smart Git Init
                if (!_gitService.IsGitRepository())
                {
                    // Ask user how to initialize
                    var initWindow = new InitGitWindow(RemoteUrlTextBox.Text);
                    initWindow.ShowDialog();

                    if (initWindow.Confirmed)
                    {
                        try
                        {
                            await _gitService.InitRepoAsync(initWindow.SelectedBranches, initWindow.RemoteUrl);
                            ModernMessageBox.Show("Git repository initialized successfully!", "Git Init", MessageBoxButton.OK, MessageBoxImage.Information);
                            
                            // Refresh Git Info
                            await LoadGitInfo(projectConfig);
                        }
                        catch (Exception ex)
                        {
                            ModernMessageBox.Show($"Failed to initialize Git: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else
                {
                    // If repo exists, check if Remote URL changed or needs setting
                    if (!string.IsNullOrWhiteSpace(RemoteUrlTextBox.Text))
                    {
                        string currentRemote = await _gitService.GetRemoteUrlAsync();
                        string newRemote = RemoteUrlTextBox.Text.Trim();

                        if (currentRemote != newRemote)
                        {
                            await _gitService.SetRemoteAsync(newRemote);
                            
                            var result = ModernMessageBox.Show(
                                "Remote URL updated! Do you want to push current branch to new remote?", 
                                "Remote Updated", 
                                MessageBoxButton.YesNo, 
                                MessageBoxImage.Question);

                            if (result)
                            {
                                try 
                                {
                                    await _gitService.PushAsync();
                                    ModernMessageBox.Show("Successfully pushed to new remote!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                catch (Exception pushEx)
                                {
                                    ModernMessageBox.Show($"Push failed: {pushEx.Message}\nMake sure you have permissions and internet connection.", "Push Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                        }
                    }
                }

                // 5. Persist .gitignore entries
                WriteGitIgnoreFile(projectPath, ignoreEntries);
                await ApplyGitIgnoreRemovals(projectPath, ignoreEntries);

                ModernMessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string[] LoadOrCreateGitIgnoreLines(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return DefaultIgnorePatterns;
            }

            try
            {
                string gitignorePath = Path.Combine(projectPath, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    File.WriteAllLines(gitignorePath, DefaultIgnorePatterns);
                    return DefaultIgnorePatterns;
                }

                var lines = File.ReadAllLines(gitignorePath)
                                .Select(l => l.Trim())
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();

                if (lines.Count == 0)
                {
                    File.WriteAllLines(gitignorePath, DefaultIgnorePatterns);
                    return DefaultIgnorePatterns;
                }

                return lines.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read .gitignore: {ex.Message}");
                return DefaultIgnorePatterns;
            }
        }

        private List<string> GetIgnoreEntriesFromTextBox()
        {
            return ExcludePatternsTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private void WriteGitIgnoreFile(string projectPath, IEnumerable<string> entries)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                return;
            }

            try
            {
                string gitignorePath = Path.Combine(projectPath, ".gitignore");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var output = new List<string>();

                void AddLine(string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var trimmed = line.Trim();
                    if (seen.Add(trimmed))
                    {
                        output.Add(trimmed);
                    }
                }

                foreach (var entry in entries)
                {
                    AddLine(entry);
                }

                foreach (var defaults in DefaultIgnorePatterns)
                {
                    AddLine(defaults);
                }

                File.WriteAllLines(gitignorePath, output);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update .gitignore: {ex.Message}");
            }
        }

        private async Task ApplyGitIgnoreRemovals(string projectPath, IEnumerable<string> entries)
        {
            if (!_gitService.IsGitRepository()) return;

            foreach (var entry in entries)
            {
                var trimmed = entry?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

                // consider folder-style entries (ending with /) or explicit directories
                bool looksLikeFolder = trimmed.EndsWith("/") || trimmed.EndsWith("\\");
                if (!looksLikeFolder)
                {
                    var pathGuess = trimmed.Replace("/", "\\");
                    var fullPath = Path.Combine(projectPath, pathGuess);
                    looksLikeFolder = Directory.Exists(fullPath);
                }

                if (!looksLikeFolder) continue;

                var normalized = trimmed.TrimEnd('/', '\\');
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                await _gitService.RemovePathFromIndexAsync(normalized);
            }
        }

        private async void AddGitIgnore_Click(object sender, RoutedEventArgs e)
        {
             string projectPath = LocalPathTextBox.Text;
             if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
             {
                 ModernMessageBox.Show("Please select a valid Local Project Path first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                 return;
             }

             var entries = GetIgnoreEntriesFromTextBox();
             if (!entries.Any(e => string.Equals(e, ".gitdeploy.config", StringComparison.OrdinalIgnoreCase)))
             {
                 entries.Add(".gitdeploy.config");
             }
             if (!entries.Any(e => string.Equals(e, ".gitdeploy.history", StringComparison.OrdinalIgnoreCase)))
             {
                 entries.Add(".gitdeploy.history");
             }

             ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, entries);
             WriteGitIgnoreFile(projectPath, entries);
             await ApplyGitIgnoreRemovals(projectPath, entries);
             ModernMessageBox.Show(".gitignore updated with app entries!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateGitPushStatusBadge(BranchStatusInfo status)
        {
            if (GitPushStatusBadge == null || GitPushStatusText == null) return;

            if (status.HasRemote && status.AheadCount > 0)
            {
                GitPushStatusBadge.Visibility = Visibility.Visible;
                GitPushStatusText.Text = $"Push pending: {status.AheadCount} commit(s)";
            }
            else
            {
                GitPushStatusBadge.Visibility = Visibility.Collapsed;
            }
        }
        private async void ReSetupButton_Click(object sender, RoutedEventArgs e)
        {
            string path = LocalPathTextBox.Text;
            if (string.IsNullOrWhiteSpace(path)) return;

            var wizard = new ProjectSetupWizard(path)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            if (wizard.ShowDialog() == true)
            {
                ModernMessageBox.Show("Project configuration re-setup successfully! 🔄", "Setup Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                await ReloadSettingsForPath(path);
            }
        }
    }
}
