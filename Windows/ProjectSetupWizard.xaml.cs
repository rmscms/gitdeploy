using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Windows
{
    public partial class ProjectSetupWizard : Window
    {
        private readonly string _projectPath;
        private readonly GitService _gitService;
        private readonly ConfigurationService _configService;

        public bool SetupCompleted { get; private set; }

        public ProjectSetupWizard(string projectPath)
        {
            InitializeComponent();
            _projectPath = projectPath;
            _gitService = new GitService();
            _configService = new ConfigurationService();
            
            GitService.SetWorkingDirectory(_projectPath);
            
            // Auto-detect if git exists
            if (_gitService.IsGitRepository())
            {
                LocalGitRadio.Content = "Existing Local Repository (Detected)";
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitRadio.IsEnabled = false;
            }
        }

        private void GitSource_Checked(object sender, RoutedEventArgs e)
        {
            if (LocalGitPanel == null || RemoteGitPanel == null) return;

            if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = Visibility.Visible;
                RemoteGitPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitPanel.Visibility = Visibility.Visible;
            }
        }

        private void EnableFtp_Checked(object sender, RoutedEventArgs e)
        {
            if (FtpConfigPanel == null) return;
            FtpConfigPanel.Visibility = (EnableFtpCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UseSsh_Checked(object sender, RoutedEventArgs e)
        {
            if (FtpPortBox == null) return;
            if (UseSshCheck.IsChecked == true && FtpPortBox.Text == "21") FtpPortBox.Text = "22";
            else if (UseSshCheck.IsChecked == false && FtpPortBox.Text == "22") FtpPortBox.Text = "21";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void TestFtp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FtpHostBox.Text) || string.IsNullOrWhiteSpace(FtpUserBox.Text))
            {
                ModernMessageBox.Show("Please enter Host and Username.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int.TryParse(FtpPortBox.Text, out int port);
            if (port <= 0) port = 21;

            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "⏳ Testing...";

            try
            {
                using (var client = new AsyncFtpClient(FtpHostBox.Text, FtpUserBox.Text, FtpPassBox.Password, port))
                {
                    await client.Connect();
                    ModernMessageBox.Show("Connection Successful! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Connection Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "🔄 Test Connection";
            }
        }

        private void BrowseFtp_Click(object sender, RoutedEventArgs e)
        {
            int.TryParse(FtpPortBox.Text, out int port);
            if (port <= 0) port = 21;

            var browser = new RemoteBrowserWindow(FtpHostBox.Text, FtpUserBox.Text, FtpPassBox.Password, port);
            if (browser.ShowDialog() == true)
            {
                FtpPathBox.Text = browser.SelectedPath;
            }
        }

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text = message;
            });
        }

        private async void Finish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // UI Busy State
                OverlayGrid.Visibility = Visibility.Visible;
                CancelButton.IsEnabled = false;
                FinishButton.IsEnabled = false;

                // 1. Git Setup
                if (!_gitService.IsGitRepository())
                {
                    if (LocalGitRadio.IsChecked == true)
                    {
                        string branch = LocalBranchName.Text.Trim();
                        if (string.IsNullOrEmpty(branch)) branch = "master";
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing local repository...");
                        await Task.Delay(500); // UI Refresh
                        
                        var initBranches = new List<string> { branch };
                        string targetBranch = TargetBranchCombo.Text.Trim();
                        if (!string.IsNullOrEmpty(targetBranch) && targetBranch != branch)
                        {
                             initBranches.Add(targetBranch);
                        }
                        
                        await _gitService.InitRepoAsync(initBranches, "");
                    }
                    else
                    {
                        string remote = RemoteUrlBox.Text.Trim();
                        if (string.IsNullOrEmpty(remote))
                        {
                            ModernMessageBox.Show("Please enter Remote URL.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                            OverlayGrid.Visibility = Visibility.Collapsed;
                            CancelButton.IsEnabled = true;
                            FinishButton.IsEnabled = true;
                            return;
                        }
                        
                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing & Connecting to Remote...");
                        var initBranches = new List<string> { "master" };
                        string targetBranch = TargetBranchCombo.Text.Trim();
                        if (!string.IsNullOrEmpty(targetBranch) && targetBranch != "master")
                        {
                             initBranches.Add(targetBranch);
                        }
                        
                        await _gitService.InitRepoAsync(initBranches, remote);
                        
                        AddLog("Pulling changes from remote...");
                        try { await _gitService.PullAsync(); } catch { }
                    }
                }
                else
                {
                    AddLog("Verifying Git configuration...");
                    CreateGitIgnore(_projectPath);
                }

                // 2. Create Project Config
                AddLog("Saving project configuration...");
                var config = new ProjectConfig
                {
                    LocalProjectPath = _projectPath,
                    DefaultSourceBranch = SourceBranchCombo.Text,
                    DefaultTargetBranch = TargetBranchCombo.Text,
                    
                    AutoInitGit = true,
                    AutoCommit = true
                };

                if (EnableFtpCheck.IsChecked == true)
                {
                    config.FtpHost = FtpHostBox.Text;
                    config.FtpUsername = FtpUserBox.Text;
                    config.FtpPassword = EncryptionService.Encrypt(FtpPassBox.Password);
                    config.RemotePath = FtpPathBox.Text;
                    config.UseSSH = UseSshCheck.IsChecked == true;
                    
                    int.TryParse(FtpPortBox.Text, out int port);
                    config.FtpPort = port > 0 ? port : 21;
                }

                _configService.SaveProjectConfig(config);

                // 3. Initial Commit (if needed)
                AddLog("Checking for changes...");
                var changes = await _gitService.GetUncommittedChangesAsync();
                if (changes.Count > 0)
                {
                    AddLog($"Committing {changes.Count} changes...");
                    await _gitService.CommitChangesAsync("Initial setup by GitDeploy Pro");
                }

                AddLog("Setup Complete!");
                await Task.Delay(500);

                SetupCompleted = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                CancelButton.IsEnabled = true;
                FinishButton.IsEnabled = true;
                ModernMessageBox.Show($"Setup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void CreateGitIgnore(string path)
        {
            try
            {
                string ignoreFile = Path.Combine(path, ".gitignore");
                if (!File.Exists(ignoreFile))
                {
                    var defaults = new[]
                    {
                        "bin/", "obj/", ".vs/", ".gitdeploy.config", ".gitdeploy.history", "*.log", "node_modules/", "vendor/"
                    };
                    File.WriteAllLines(ignoreFile, defaults);
                }
            }
            catch { }
        }
    }
}