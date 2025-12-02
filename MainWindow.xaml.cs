using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; 
using System.Windows.Media; 
using GitDeployPro.Pages;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using Button = System.Windows.Controls.Button;

namespace GitDeployPro
{
    public partial class MainWindow : Window
    {
        private ConfigurationService _configService;

        public MainWindow()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            LoadRecentProjects();
            
            ContentFrame.Navigate(new DashboardPage());
        }

        private void LoadRecentProjects()
        {
            var config = _configService.LoadGlobalConfig();
            
            // Set current project info in the button
            if (!string.IsNullOrEmpty(config.LastProjectPath))
            {
                string name = System.IO.Path.GetFileName(config.LastProjectPath);
                ProjectNameText.Text = name;
                
                ProjectInitialText.Text = GetProjectInitial(name);
                ProjectAvatarBorder.Background = GetProjectColor(name);

                GitService.SetWorkingDirectory(config.LastProjectPath);
                HistoryService.SetWorkingDirectory(config.LastProjectPath);
            }
            else
            {
                ProjectNameText.Text = "Select Project";
                ProjectInitialText.Text = "?";
                ProjectAvatarBorder.Background = System.Windows.Media.Brushes.Gray;
            }

            // Populate Recent Projects List
            RecentProjectsList.ItemsSource = null;
            if (config.RecentProjects != null && config.RecentProjects.Any())
            {
                var recentItems = config.RecentProjects
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                    .OrderByDescending(entry => entry.LastOpenedUtc)
                    .Select(entry =>
                    {
                        string name = System.IO.Path.GetFileName(entry.Path);
                        return new
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? entry.Path : name,
                            Path = entry.Path,
                            Initial = GetProjectInitial(name),
                            ColorBrush = GetProjectColor(name)
                        };
                    })
                    .ToList();
                
                RecentProjectsList.ItemsSource = recentItems;
            }
        }

        private string GetProjectInitial(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.Substring(0, 1).ToUpper();
        }

        private SolidColorBrush GetProjectColor(string name)
        {
            if (string.IsNullOrEmpty(name)) return System.Windows.Media.Brushes.Gray;

            int hash = name.GetHashCode();
            
            // Colors (Hex strings)
            var colors = new[] 
            {
                "#3574F0", // Blue
                "#E05555", // Red
                "#579A57", // Green
                "#E59500", // Orange
                "#9B59B6", // Purple
                "#00ACC1", // Cyan
                "#F06292"  // Pink
            };

            int index = Math.Abs(hash) % colors.Length;
            string colorCode = colors[index];
            
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorCode);
                return new SolidColorBrush(color);
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }

        private void ProjectSelectorBtn_Click(object sender, RoutedEventArgs e)
        {
            ProjectMenuPopup.IsOpen = !ProjectMenuPopup.IsOpen;
        }

        private void RecentProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string path = btn.Tag.ToString();
                SwitchProject(path);
                ProjectMenuPopup.IsOpen = false;
            }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            ProjectMenuPopup.IsOpen = false;
            
            try 
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "Select Project Folder";
                    dialog.ShowNewFolderButton = true;
                    
                    System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                    
                    if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        SwitchProject(dialog.SelectedPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error opening project: {ex.Message}");
            }
        }

        private void SwitchProject(string path)
        {
            _configService.AddRecentProject(path);
            LoadRecentProjects(); // Refresh name and list

            GitService.SetWorkingDirectory(path);
            HistoryService.SetWorkingDirectory(path);

            CheckAndShowSetupWizard(path);

            NavigateToDashboard();
        }

        private void CheckAndShowSetupWizard(string path)
        {
            // Ensure we're on UI thread and window is loaded
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Check if configured
                    var gitService = new GitService();
                    var config = _configService.LoadProjectConfig(path);
                    
                    bool isGitRepo = gitService.IsGitRepository();
                    
                    // If not a git repo OR (is a repo but has no config file), show wizard
                    // BUT: If it is a git repo, we should be careful. Maybe user just opened an existing repo.
                    // Let's only force wizard if:
                    // 1. Not a git repo at all
                    // 2. Is a git repo but we have never seen it (no config) AND it has no remotes (fresh init)?
                    // Actually, the requirement is "Setup Wizard" for "New Projects".
                    // If a folder has .git, it's technically initialized.
                    // If .gitdeploy.config is missing, it just means we haven't configured FTP/Deployment settings.
                    // So, if IsGitRepo is true, we should probably SKIP the wizard or make it optional?
                    // User said: "I opened a project with .git folder but wizard popped up. It shouldn't."

                    bool shouldShowWizard = !isGitRepo;

                    // If it IS a repo, but no config, maybe just let them use it as a Git Client?
                    // Only show wizard if it's NOT a repo.
                    // If they want to configure FTP later, they can use Settings or "Run Setup" button.
                    
                    if (shouldShowWizard)
                    {
                        var wizard = new ProjectSetupWizard(path)
                        {
                            Owner = this
                        };
                        
                        // Ensure owner is visible
                        if (!this.IsVisible) this.Show();

                        wizard.ShowDialog();
                        
                        if (wizard.SetupCompleted)
                        {
                            // Reload everything and refresh Dashboard
                            LoadRecentProjects();
                            NavigateToDashboard();
                        }
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        public void NavigateToDashboard()
        {
            LoadRecentProjects();
            ContentFrame.Navigate(new DashboardPage());
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e) => NavigateToDashboard();
        private void Deploy_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new DeployPage());
        private void DirectUpload_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new DirectUploadPage());
        private void Terminal_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new TerminalPage());
        private void Git_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new GitPage());
        private void History_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new HistoryPage());
        private void Settings_Click(object sender, RoutedEventArgs e) => ContentFrame.Navigate(new SettingsPage());
        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
    }
}
