using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GitDeployPro.Services;
using GitDeployPro.Models;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class TerminalPage : Page
    {
        private ConfigurationService _configService;
        private string _currentProjectPath;
        private ObservableCollection<TerminalCommandPreset> _commandPresets = new();

        public TerminalPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            Loaded += TerminalPage_Loaded;
            Unloaded += TerminalPage_Unloaded;
        }

        private void TerminalPage_Loaded(object sender, RoutedEventArgs e)
        {
            var globalConfig = _configService.LoadGlobalConfig();
            
            if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
            {
                _currentProjectPath = globalConfig.LastProjectPath;
                 Terminal.SetProjectPath(_currentProjectPath);
            }

            LoadSshProfiles();
            LoadCommandPresets();
            TerminalPresetStore.PresetsChanged += TerminalPresetStore_PresetsChanged;
        }

        private void TerminalPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TerminalPresetStore.PresetsChanged -= TerminalPresetStore_PresetsChanged;
        }

        private void TerminalPresetStore_PresetsChanged()
        {
            Dispatcher.Invoke(LoadCommandPresets);
        }

        private void LoadSshProfiles()
        {
            var profiles = _configService.LoadConnections()
                                         .Where(p => p.UseSSH)
                                         .ToList();
            
            // Add current project config if valid
            if (!string.IsNullOrEmpty(_currentProjectPath))
            {
                var projectConfig = _configService.LoadProjectConfig(_currentProjectPath);
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
            }
            
            // Add Local Terminal Option
            var localProfileOption = new ConnectionProfile
            {
                Name = "Local Terminal",
                Host = "Windows CMD",
                UseSSH = false,
                Id = "LocalCMD"
            };
            profiles.Add(localProfileOption);
            
            SshProfilesCombo.ItemsSource = profiles;

            // Select Local Terminal by default if available, otherwise first item
            var defaultProfile = profiles.FirstOrDefault(p => p.Id == "LocalCMD");
            if (defaultProfile != null)
            {
                SshProfilesCombo.SelectedItem = defaultProfile;
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
                await Terminal.ConnectLocal();
            }
            // SSH profiles wait for Quick Connect click
        }

        private async void QuickConnect_Click(object sender, RoutedEventArgs e)
        {
             if (SshProfilesCombo.SelectedItem is ConnectionProfile profile)
            {
                 if (profile.Id == "LocalCMD")
                 {
                     await Terminal.ConnectLocal();
                 }
                 else
                 {
                     string password = EncryptionService.Decrypt(profile.Password);
                     await Terminal.ConnectAsync(profile.Host, profile.Username, password, profile.Port);
                 }
            }
            else
            {
                GitDeployPro.Controls.ModernMessageBox.Show("Please select an SSH profile first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void LoadCommandPresets()
        {
            var previous = PresetComboBox?.SelectedValue?.ToString();
            _commandPresets = TerminalPresetStore.LoadPresets();
            if (PresetComboBox != null)
            {
                PresetComboBox.ItemsSource = _commandPresets;
                if (!string.IsNullOrEmpty(previous))
                {
                    var match = _commandPresets.FirstOrDefault(p => p.Id == previous);
                    if (match != null)
                    {
                        PresetComboBox.SelectedItem = match;
                    }
                }
                if (PresetComboBox.SelectedIndex == -1 && _commandPresets.Count > 0)
                {
                    PresetComboBox.SelectedIndex = 0;
                }
            }
        }

        private void AddPresetButton_Click(object sender, RoutedEventArgs e)
        {
            var title = (PresetTitleBox.Text ?? string.Empty).Trim();
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
            {
                System.Windows.MessageBox.Show("Please enter both a title and a command.", "Command Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preset = new TerminalCommandPreset
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Command = command
            };
            _commandPresets.Add(preset);
            TerminalPresetStore.SavePresets(_commandPresets);
            PresetTitleBox.Text = string.Empty;
            PresetCommandBox.Text = string.Empty;
            PresetComboBox.SelectedItem = preset;
        }

        private void UsePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox?.SelectedItem is TerminalCommandPreset preset)
            {
                SendPresetToTerminals(preset.Command);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox?.SelectedItem is TerminalCommandPreset preset)
            {
                var existing = _commandPresets.FirstOrDefault(p => p.Id == preset.Id);
                if (existing != null)
                {
                    _commandPresets.Remove(existing);
                    TerminalPresetStore.SavePresets(_commandPresets);
                }
            }
        }

        private void SendPresetToTerminals(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            if (BroadcastAllCheckBox?.IsChecked == true)
            {
                Controls.TerminalControl.BroadcastCommand(command);
            }
            else
            {
                Terminal?.InjectCommandText(command);
            }
        }

        private void DetachTerminalPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new TerminalPage(), "Terminal • Detached");
            window.Show();
        }
    }
}
