using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Services;

namespace GitDeployPro.Pages
{
    public partial class HistoryPage : Page
    {
        private HistoryService _historyService;
        private GitService _gitService;
        private List<DeploymentRecord> _currentHistoryItems;

        public HistoryPage()
        {
            InitializeComponent();
            _historyService = new HistoryService();
            _gitService = new GitService();
            _currentHistoryItems = new List<DeploymentRecord>();
            Loaded += HistoryPage_Loaded;
        }

        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= HistoryPage_Loaded;
            await LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
        {
            var historyItems = _historyService.GetHistory();

            // If no local history exists or it's empty, populate from Git commit history
            if ((historyItems == null || !historyItems.Any()) && _gitService.IsGitRepository())
            {
                historyItems = await BuildCommitsAsHistoryAsync();
            }
            
            _currentHistoryItems = historyItems ?? new List<DeploymentRecord>();
            HistoryItemsControl.ItemsSource = _currentHistoryItems;
        }

        private async Task<List<DeploymentRecord>> BuildCommitsAsHistoryAsync()
        {
            try
            {
                // Fetch all recent commits (up to 1000) to populate history
                var commits = await _gitService.GetCommitHistoryAsync(1000);
                var branch = await _gitService.GetCurrentBranchAsync();

                return commits.Select((commit, index) => new DeploymentRecord
                {
                    Id = index + 1,
                    Title = $"{commit.ShortHash} - {commit.Message}",
                    Date = commit.Date,
                    FilesCount = 0, // Git log doesn't easily give file count without extra commands per commit
                    Branch = branch ?? "",
                    Status = "Success",
                    Files = new List<string>(), // Empty for now as fetching files per commit is expensive
                    CommitHash = commit.FullHash
                }).ToList();
            }
            catch
            {
                return new List<DeploymentRecord>();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadHistoryAsync();
            ModernMessageBox.Show("History refreshed!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int id)
            {
                var record = _currentHistoryItems.FirstOrDefault(x => x.Id == id);
                if (record != null)
                {
                    var detailsWindow = new HistoryDetailsWindow(record);
                    detailsWindow.ShowDialog();
                }
            }
        }

        private async void Rollback_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int id)
            {
                var record = _currentHistoryItems.FirstOrDefault(x => x.Id == id);
                if (record == null) return;

                if (string.IsNullOrEmpty(record.CommitHash))
                {
                     ModernMessageBox.Show("Cannot rollback this item (No commit hash found).", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                }

                var result = ModernMessageBox.Show(
                    $"Are you sure you want to rollback this commit?\n\nCommit: {record.Title}\n\nThis will create a NEW commit that reverses the changes.", 
                    "Confirm Rollback", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Question);
                
                if (result)
                {
                    try
                    {
                        await _gitService.RevertCommitAsync(record.CommitHash);
                        ModernMessageBox.Show("Rollback successful! A new revert commit has been created.\n\nPlease deploy the changes to apply them to the server.", 
                            "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        await LoadHistoryAsync(); // Refresh list to show new revert commit
                    }
                    catch (Exception ex)
                    {
                        ModernMessageBox.Show($"Rollback failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}
