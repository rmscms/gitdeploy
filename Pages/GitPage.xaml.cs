using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class GitPage : Page
    {
        private GitService _gitService;

        public GitPage()
        {
            InitializeComponent();
            _gitService = new GitService();
            LoadData();
        }

        private async void LoadData()
        {
            try
            {
                if (!_gitService.IsGitRepository())
                {
                    RemoteUrlText.Text = "Not a Git Repository";
                    RemoteUrlText.Foreground = System.Windows.Media.Brushes.Gray;
                    DisableControls();
                    return;
                }

                // Load Remote URL
                var remote = await _gitService.GetRemoteUrlAsync();
                if (string.IsNullOrEmpty(remote))
                {
                    RemoteUrlText.Text = "No remote 'origin' configured";
                    RemoteUrlText.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    RemoteUrlText.Text = remote;
                }

                // Load Current Branch
                var branch = await _gitService.GetCurrentBranchAsync();
                BranchText.Text = $"Current Branch: {branch}";

                // Load Tags
                var tags = await _gitService.GetTagsAsync();
                TagsListBox.Items.Clear();
                if (tags.Count == 0)
                {
                    TagsListBox.Items.Add(new ListBoxItem { Content = "No tags found", IsEnabled = false, Foreground = System.Windows.Media.Brushes.Gray });
                }
                else
                {
                    foreach (var tag in tags)
                    {
                        TagsListBox.Items.Add(new ListBoxItem { Content = tag });
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading Git data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DisableControls()
        {
            PullButton.IsEnabled = false;
            PushButton.IsEnabled = false;
            PushTagsButton.IsEnabled = false;
            CreateTagButton.IsEnabled = false;
        }

        private async void PullButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PullButton.IsEnabled = false;
                PullButton.Content = "⏳ Pulling...";

                await _gitService.PullAsync();
                
                ModernMessageBox.Show("Successfully pulled changes from remote! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadData(); // Refresh UI
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Pull failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PullButton.IsEnabled = true;
                PullButton.Content = "⬇ Pull";
            }
        }

        private async void PushButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PushButton.IsEnabled = false;
                PushButton.Content = "⏳ Pushing...";

                await _gitService.PushAsync();
                
                ModernMessageBox.Show("Successfully pushed changes to remote! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Push failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PushButton.IsEnabled = true;
                PushButton.Content = "⬆ Push";
            }
        }

        private async void PushTagsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PushTagsButton.IsEnabled = false;
                PushTagsButton.Content = "⏳ Pushing Tags...";

                await _gitService.PushTagsAsync();
                
                ModernMessageBox.Show("Successfully pushed all tags to remote! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Push tags failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PushTagsButton.IsEnabled = true;
                PushTagsButton.Content = "⬆ Push All Tags";
            }
        }

        private async void CreateTagButton_Click(object sender, RoutedEventArgs e)
        {
            string tagName = TagNameBox.Text.Trim();
            string message = TagMessageBox.Text.Trim();

            if (string.IsNullOrEmpty(tagName))
            {
                ModernMessageBox.Show("Please enter a Tag Name.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(message)) message = $"Release {tagName}";

            try
            {
                CreateTagButton.IsEnabled = false;
                CreateTagButton.Content = "⏳ Creating...";

                await _gitService.CreateTagAsync(tagName, message);
                
                // Auto push tags if checkbox enabled (or we can just push tags always for this button as it says Create & Push)
                // The button label says "Create & Push Tag", so we push.
                await _gitService.PushTagsAsync();

                ModernMessageBox.Show($"Tag '{tagName}' created and pushed successfully! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                
                TagNameBox.Text = "";
                TagMessageBox.Text = "";
                LoadData(); // Refresh tags list
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to create tag: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CreateTagButton.IsEnabled = true;
                CreateTagButton.Content = "Create & Push Tag";
            }
        }

        private async void CloneRepoButton_Click(object sender, RoutedEventArgs e)
        {
            var cloneWindow = new CloneRepoWindow();
            
            // Safer Owner setting
            if (System.Windows.Application.Current?.MainWindow != null && 
                System.Windows.Application.Current.MainWindow.IsVisible)
            {
                cloneWindow.Owner = System.Windows.Application.Current.MainWindow;
            }
            else
            {
                cloneWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            if (cloneWindow.ShowDialog() != true)
            {
                return;
            }

            try
            {
                CloneRepoButton.IsEnabled = false;
                CloneRepoButton.Content = "⏳ Cloning...";

                await _gitService.CloneRepositoryAsync(cloneWindow.RemoteUrl, cloneWindow.LocalPath);

                var configurationService = new ConfigurationService();
                configurationService.AddRecentProject(cloneWindow.LocalPath);
                GitService.SetWorkingDirectory(cloneWindow.LocalPath);
                HistoryService.SetWorkingDirectory(cloneWindow.LocalPath);

                ModernMessageBox.Show("Repository cloned successfully! ✅", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Navigate to dashboard to refresh context
                if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.NavigateToDashboard();
                }
                else 
                {
                    LoadData();
                }
            }
            catch (Exception ex)
            {
                // Show detailed error including inner exception
                var msg = ex.Message;
                if (ex.InnerException != null)
                {
                    msg += $"\n\nDetails: {ex.InnerException.Message}";
                }
                
                ModernMessageBox.Show($"Clone failed:\n{msg}", "Clone Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CloneRepoButton.IsEnabled = true;
                CloneRepoButton.Content = "Clone / Connect";
            }
        }

        private void DetachGitPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new GitPage(), "GitHub • Detached");
            window.Show();
        }
    }
}

