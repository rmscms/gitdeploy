using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services;
using GitDeployPro.Models;
using GitDeployPro.Controls;
using MahApps.Metro.Controls;
using System.Linq;
using System.Collections.Generic;

namespace GitDeployPro.Windows
{
    public partial class TerminalWindow : MetroWindow
    {
        private ConfigurationService _configService;
        private string _projectPath;

        public TerminalWindow(string projectPath)
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _projectPath = projectPath;
            
            MyTerminal.SetProjectPath(projectPath);
            MyTerminal.DetachButton.Visibility = Visibility.Collapsed; // Hide pop-out button in new window

            LoadSshProfiles();
        }

        private void LoadSshProfiles()
        {
            var profiles = _configService.LoadConnections()
                                         .Where(p => p.UseSSH)
                                         .ToList();
            
            // Add current project config if valid
            var projectConfig = _configService.LoadProjectConfig(_projectPath);
            if (projectConfig != null && !string.IsNullOrEmpty(projectConfig.FtpHost) && projectConfig.UseSSH)
            {
                 var currentProfile = new ConnectionProfile
                 {
                     Name = "Current Project",
                     Host = projectConfig.FtpHost,
                     Port = projectConfig.FtpPort,
                     Username = projectConfig.FtpUsername,
                     Password = projectConfig.FtpPassword, // Already encrypted in config
                     UseSSH = true,
                     Id = "CurrentProject"
                 };
                 profiles.Insert(0, currentProfile);
            }
            
            // Add Local Terminal Option
            var localProfile = new ConnectionProfile
            {
                Name = "Local Terminal",
                Host = "Windows CMD",
                UseSSH = false,
                Id = "LocalCMD"
            };
            profiles.Add(localProfile);

            SshProfilesCombo.ItemsSource = profiles;
            
            // Select Local Terminal by default if available, otherwise first item
            var localProfileOption = profiles.FirstOrDefault(p => p.Id == "LocalCMD");
            if (localProfileOption != null)
            {
                SshProfilesCombo.SelectedItem = localProfileOption;
            }
            else if (profiles.Count > 0)
            {
                SshProfilesCombo.SelectedIndex = 0;
            }
        }

        private async void SshProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SshProfilesCombo.SelectedItem is ConnectionProfile profile && profile.Id == "LocalCMD")
            {
                await MyTerminal.ConnectLocal();
            }
            // SSH profiles wait for Quick Connect click
        }

        private async void QuickConnect_Click(object sender, RoutedEventArgs e)
        {
             if (SshProfilesCombo.SelectedItem is ConnectionProfile profile)
            {
                 if (profile.Id == "LocalCMD")
                 {
                     await MyTerminal.ConnectLocal();
                 }
                 else
                 {
                     string password = EncryptionService.Decrypt(profile.Password);
                     await MyTerminal.ConnectAsync(profile.Host, profile.Username, password, profile.Port);
                 }
            }
            else
            {
                ModernMessageBox.Show("Please select an SSH profile first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ManageProfiles_Click(object sender, RoutedEventArgs e)
        {
            var win = new ConnectionManagerWindow();
            if (win.ShowDialog() == true)
            {
                LoadSshProfiles(); // Refresh list after editing
            }
        }
    }
}
