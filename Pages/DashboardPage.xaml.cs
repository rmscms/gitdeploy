using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GitDeployPro.Controls;
using GitDeployPro.Services;

namespace GitDeployPro.Pages
{
    public partial class DashboardPage : Page
    {
        private GitService _gitService;
        private HistoryService _historyService;
        private ConfigurationService _configService;

        public DashboardPage()
        {
            InitializeComponent();
            _gitService = new GitService();
            _historyService = new HistoryService();
            _configService = new ConfigurationService();
            LoadDashboardData();
        }

        private async void LoadDashboardData()
        {
            try
            {
                // 1. Project Info
                string currentPath = "No Project Selected";
                
                // Load from Global Config (Last Project)
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath) && Directory.Exists(globalConfig.LastProjectPath))
                {
                    currentPath = globalConfig.LastProjectPath;
                    
                    // Update GitService to point to this path
                    GitService.SetWorkingDirectory(currentPath);
                }
                else
                {
                    // Fallback to current directory if not set
                    // But usually we want to show "Select Project" if nothing is set
                    if (currentPath == "No Project Selected")
                    {
                         currentPath = Directory.GetCurrentDirectory();
                         // Check if this fallback is actually a repo
                         if (!Directory.Exists(Path.Combine(currentPath, ".git")))
                         {
                             currentPath = "Please select a project in Settings";
                         }
                    }
                }

                ProjectPathText.Text = $"Path: {currentPath}";

                if (!_gitService.IsGitRepository())
                {
                    CurrentBranchText.Text = "Git Repository not found";
                    return;
                }

                // 2. Git Stats
                var branch = await _gitService.GetCurrentBranchAsync();
                CurrentBranchText.Text = $"Current Branch: {branch}";

                var commits = await _gitService.GetTotalCommitsAsync();
                CommitsCount.Text = commits.ToString();

                var changes = await _gitService.GetUncommittedChangesAsync();
                ChangedFilesCount.Text = changes.Count.ToString();

                // 3. History Stats
                var lastDeploy = _historyService.GetLastDeploy();
                if (lastDeploy != null)
                {
                    LastDeployText.Text = $"{lastDeploy.Date:MM/dd HH:mm}";
                }
                else
                {
                    LastDeployText.Text = "Never";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void QuickDeploy_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.Navigate(new DeployPage());
        }

        private void RefreshGit_Click(object sender, RoutedEventArgs e)
        {
            LoadDashboardData();
            ModernMessageBox.Show("Dashboard data refreshed!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.Navigate(new SettingsPage());
        }
    }
}
