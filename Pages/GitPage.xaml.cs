using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Services;

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
    }
}

