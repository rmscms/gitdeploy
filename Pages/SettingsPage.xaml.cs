using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Theme;
using GitDeployPro.Services.Update;
using GitDeployPro.Services.Telegram;
using GitDeployPro.Windows;
using GitDeployPro.Services.Vpn;
using System.Diagnostics;
using System.Windows.Forms; // For FolderBrowserDialog

namespace GitDeployPro.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly ConfigurationService _configService;
        private readonly GitService _gitService;
        private readonly AutoStartService _autoStartService = new();
        private readonly ProjectSetupResetService _setupResetService = new();
        private static readonly string[] DefaultIgnorePatterns = new[]
        {
            "bin/", "obj/", ".vs/", ".idea/", ".vscode/", ".cursor/", "packages/", "dist/", "build/", "node_modules/", ".env", "*.log", "/vendor/", ".gitdeploy.config", ".gitdeploy.history"
        };

        private bool _suppressLanguageComboChange;
        private bool _suppressEditorModeChange;
        private bool _suppressTerminalAppearanceChange;
        private readonly List<string> _draftAssignedFtpIds = new();
        private string _draftDefaultFtpId = string.Empty;
        private bool _draftFtpConfirmed = true;
        private List<ConnectionProfile> _remoteFtpProfiles = new();
        private bool _telegramTokenDirty;
        private bool _openRouterKeyDirty;
        private bool _vpnStatusHooked;
        private bool _suppressVpnProviderChange;

        public SettingsPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _gitService = new GitService();
            LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLanguageUiTexts();
            Unloaded += SettingsPage_Unloaded;
            ShowSettingsSection("general");
            LoadSettings();
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_vpnStatusHooked)
            {
                VpnKeepAliveService.Instance.StatusChanged -= VpnKeepAlive_StatusChanged;
                _vpnStatusHooked = false;
            }
        }

        private void SettingsNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string section)
            {
                ShowSettingsSection(section);
            }
        }

        private void ShowSettingsSection(string section)
        {
            section = (section ?? "general").Trim().ToLowerInvariant();

            if (SettingsPanelGeneral != null)
            {
                SettingsPanelGeneral.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelServer != null)
            {
                SettingsPanelServer.Visibility = section == "server" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelGit != null)
            {
                SettingsPanelGit.Visibility = section == "git" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelTerminal != null)
            {
                SettingsPanelTerminal.Visibility = section == "terminal" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelTelegram != null)
            {
                SettingsPanelTelegram.Visibility = section == "telegram" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelAiAgent != null)
            {
                SettingsPanelAiAgent.Visibility = section == "ai-agent" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelVpn != null)
            {
                SettingsPanelVpn.Visibility = section == "vpn" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SettingsPanelThemes != null)
            {
                SettingsPanelThemes.Visibility = section == "themes" ? Visibility.Visible : Visibility.Collapsed;
            }

            SetNavActive(NavGeneralButton, section == "general");
            SetNavActive(NavServerButton, section == "server");
            SetNavActive(NavGitButton, section == "git");
            SetNavActive(NavTerminalButton, section == "terminal");
            SetNavActive(NavTelegramButton, section == "telegram");
            SetNavActive(NavAiAgentButton, section == "ai-agent");
            SetNavActive(NavVpnButton, section == "vpn");
            SetNavActive(NavThemesButton, section == "themes");

            if (SettingsSectionSubtitle != null)
            {
                SettingsSectionSubtitle.Text = section switch
                {
                    "server" => "FTP/SFTP connection profile and setup recovery.",
                    "git" => "Remote, branches, deploy automation, and ignore patterns.",
                    "terminal" => "Terminal autocomplete commands and scopes.",
                    "telegram" => Loc.T("settings.telegramHint"),
                    "ai-agent" => Loc.T("settings.aiAgentHint"),
                    "vpn" => Loc.T("vpn.subtitle"),
                    "themes" => "Import Deploy theme packs and manage custom skins.",
                    _ => "Project path, startup, updates, and danger zone."
                };
            }

            if (section == "themes")
            {
                RefreshThemePacksList();
            }

            if (section == "git")
            {
                RefreshGitIgnoreFromDisk();
            }

            if (section == "terminal")
            {
                var projectPath = _configService.LoadGlobalConfig().LastProjectPath;
                TerminalSuggestionsPanel?.Reload(projectPath);
            }
        }

        private void SetNavActive(System.Windows.Controls.Button? button, bool active)
        {
            if (button == null)
            {
                return;
            }

            button.Style = (Style)FindResource(active ? "Settings.NavButton.Active" : "Settings.NavButton");
        }

        private async void LoadSettings()
        {
            try
            {
                var globalConfig = _configService.LoadGlobalConfig();
                if (!string.IsNullOrEmpty(globalConfig.LastProjectPath))
                {
                    await ReloadSettingsForPath(globalConfig.LastProjectPath);
                }
                else
                {
                    UpdateDangerZoneUi(null);
                }
                var startupEnabled = _autoStartService.IsEnabled();
                LaunchOnStartupCheckBox.IsChecked = startupEnabled;
                ShowBackupLocalhostWarningCheckBox.IsChecked = globalConfig.ShowBackupSchedulerLocalhostWarning;
                MinimizeToTrayCheckBox.IsChecked = globalConfig.MinimizeToTray;
                if (globalConfig.LaunchOnStartup != startupEnabled)
                {
                    _configService.UpdateGlobalConfig(cfg => cfg.LaunchOnStartup = startupEnabled);
                }

                RefreshStartupAudit();
                RefreshUpdateStatus(globalConfig);
                RefreshThemePacksList();
                LoadLanguageCombo(globalConfig.UiLanguage);
                LoadWorkspacePreferences();
                RefreshLanguageUiTexts();
                LoadTelegramSettings(globalConfig);
                LoadVpnSettings(globalConfig);
                TerminalSuggestionsPanel?.Reload(globalConfig.LastProjectPath);
                if (SshKeyPathTextBox != null)
                {
                    SshKeyPathTextBox.Text = globalConfig.DefaultSshKeyPath ?? string.Empty;
                }

                if (DeployDefaultWorkersTextBox != null)
                {
                    DeployDefaultWorkersTextBox.Text = Math.Clamp(
                        globalConfig.DeployDefaultWorkers > 0 ? globalConfig.DeployDefaultWorkers : 8,
                        1,
                        8).ToString();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshLanguageUiTexts()
        {
            // Chrome stays English; Loc.T already resolves non-tip keys from the EN pack.
            if (LanguageSectionTitle != null)
            {
                LanguageSectionTitle.Text = Loc.T("settings.language");
            }

            if (LanguageSectionHint != null)
            {
                LanguageSectionHint.Text = Loc.T("settings.languageHint");
            }

            if (LanguageLabel != null)
            {
                LanguageLabel.Text = Loc.T("settings.language");
            }

            if (LanguageNoteText != null)
            {
                LanguageNoteText.Text = Loc.T("settings.languageNote");
            }

            if (ProjectKindSectionTitle != null)
            {
                ProjectKindSectionTitle.Text = Loc.T("settings.projectKind");
            }

            if (ProjectKindSectionHint != null)
            {
                ProjectKindSectionHint.Text = Loc.T("settings.projectKindHint");
            }

            if (ProjectKindLabel != null)
            {
                ProjectKindLabel.Text = Loc.T("settings.projectKind");
            }

            if (ProjectKindNoteText != null)
            {
                ProjectKindNoteText.Text = Loc.T("settings.projectKindNote");
            }

            ReloadProjectKindComboItems();

            if (EditorPrefTitle != null)
            {
                EditorPrefTitle.Text = Loc.T("settings.editor");
            }

            if (EditorPrefHint != null)
            {
                EditorPrefHint.Text = Loc.T("settings.editorHint");
            }

            if (EditorOpenDockRadio != null)
            {
                EditorOpenDockRadio.Content = Loc.T("settings.editorDock");
            }

            if (EditorOpenFloatRadio != null)
            {
                EditorOpenFloatRadio.Content = Loc.T("settings.editorFloat");
            }

            if (TerminalAppearanceTitle != null)
            {
                TerminalAppearanceTitle.Text = Loc.T("settings.terminalAppearance");
            }

            if (TerminalAppearanceHint != null)
            {
                TerminalAppearanceHint.Text = Loc.T("settings.terminalAppearanceHint");
            }

            if (TerminalFontLabel != null)
            {
                TerminalFontLabel.Text = Loc.T("settings.terminalFont");
            }

            if (TerminalColorLabel != null)
            {
                TerminalColorLabel.Text = Loc.T("settings.terminalColor");
            }

            if (!_suppressLanguageComboChange)
            {
                LoadLanguageCombo(LocalizationService.Instance.Language);
            }
        }

        private void LoadLanguageCombo(string? selectedCode = null)
        {
            if (UiLanguageComboBox == null)
            {
                return;
            }

            _suppressLanguageComboChange = true;
            try
            {
                var options = LocalizationService.Instance.GetLanguageOptions().ToList();
                UiLanguageComboBox.ItemsSource = options;
                UiLanguageComboBox.DisplayMemberPath = string.Empty;
                UiLanguageComboBox.SelectedValuePath = nameof(LocalizationService.LanguageOption.Code);
                var code = string.IsNullOrWhiteSpace(selectedCode)
                    ? LocalizationService.Instance.Language
                    : selectedCode;
                var selected = options.FirstOrDefault(o =>
                    string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase))
                    ?? options.FirstOrDefault();
                UiLanguageComboBox.SelectedItem = selected;
            }
            finally
            {
                _suppressLanguageComboChange = false;
            }
        }

        private void UiLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLanguageComboChange)
            {
                return;
            }

            string? code = UiLanguageComboBox?.SelectedValue as string
                           ?? (UiLanguageComboBox?.SelectedItem as LocalizationService.LanguageOption)?.Code;
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            LocalizationService.Instance.SetLanguage(code, persist: true, raiseUi: true);
            RefreshLanguageUiTexts();
        }

        private void ReloadProjectKindComboItems()
        {
            if (ProjectKindComboBox == null)
            {
                return;
            }

            var selected = ReadProjectKindFromCombo();
            ProjectKindComboBox.Items.Clear();
            foreach (ProjectKind kind in Enum.GetValues(typeof(ProjectKind)))
            {
                ProjectKindComboBox.Items.Add(new ComboBoxItem
                {
                    Tag = kind,
                    Content = ProjectKindLabelText(kind)
                });
            }

            SelectProjectKind(selected);
        }

        private static string ProjectKindLabelText(ProjectKind kind)
        {
            return kind switch
            {
                ProjectKind.WindowsDesktop => Loc.T("settings.projectKind.windowsDesktop"),
                ProjectKind.NoDeploy => Loc.T("settings.projectKind.noDeploy"),
                _ => Loc.T("settings.projectKind.webFtp")
            };
        }

        private void SelectProjectKind(ProjectKind kind)
        {
            if (ProjectKindComboBox == null)
            {
                return;
            }

            if (ProjectKindComboBox.Items.Count == 0)
            {
                ReloadProjectKindComboItems();
            }

            foreach (ComboBoxItem item in ProjectKindComboBox.Items)
            {
                if (item.Tag is ProjectKind tagged && tagged == kind)
                {
                    ProjectKindComboBox.SelectedItem = item;
                    return;
                }
            }

            if (ProjectKindComboBox.Items.Count > 0)
            {
                ProjectKindComboBox.SelectedIndex = 0;
            }
        }

        private ProjectKind ReadProjectKindFromCombo()
        {
            if (ProjectKindComboBox?.SelectedItem is ComboBoxItem item && item.Tag is ProjectKind kind)
            {
                return kind;
            }

            return ProjectKind.WebFtpDeploy;
        }

        private void LoadWorkspacePreferences()
        {
            _suppressEditorModeChange = true;
            _suppressTerminalAppearanceChange = true;
            try
            {
                var floatMode = WorkspacePreferencesStore.OpenEditorInFloat();
                if (EditorOpenFloatRadio != null)
                {
                    EditorOpenFloatRadio.IsChecked = floatMode;
                }

                if (EditorOpenDockRadio != null)
                {
                    EditorOpenDockRadio.IsChecked = !floatMode;
                }

                var (fontSize, foreground) = WorkspacePreferencesStore.LoadAppearance();
                SelectComboByContent(SettingsTerminalFontCombo, fontSize.ToString("0"));
                SelectComboByTag(SettingsTerminalColorCombo, foreground);
            }
            finally
            {
                _suppressEditorModeChange = false;
                _suppressTerminalAppearanceChange = false;
            }
        }

        private static void SelectComboByContent(System.Windows.Controls.ComboBox? combo, string content)
        {
            if (combo == null)
            {
                return;
            }

            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static void SelectComboByTag(System.Windows.Controls.ComboBox? combo, string tag)
        {
            if (combo == null)
            {
                return;
            }

            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void EditorOpenModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEditorModeChange)
            {
                return;
            }

            var mode = EditorOpenFloatRadio?.IsChecked == true
                ? WorkspacePreferencesStore.EditorModeFloat
                : WorkspacePreferencesStore.EditorModeDock;
            WorkspacePreferencesStore.SaveEditorOpenMode(mode);
        }

        private void SettingsTerminalAppearance_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTerminalAppearanceChange)
            {
                return;
            }

            double fontSize = WorkspacePreferencesStore.DefaultFontSize;
            if (SettingsTerminalFontCombo?.SelectedItem is ComboBoxItem fontItem
                && double.TryParse(fontItem.Content?.ToString(), out var parsed))
            {
                fontSize = parsed;
            }

            var foreground = WorkspacePreferencesStore.DefaultForeground;
            if (SettingsTerminalColorCombo?.SelectedItem is ComboBoxItem colorItem
                && colorItem.Tag is string tag
                && !string.IsNullOrWhiteSpace(tag))
            {
                foreground = tag;
            }

            WorkspacePreferencesStore.SaveAppearance(fontSize, foreground);
        }

        private bool _suppressAppThemeComboChange;

        private sealed class ThemeListItem
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string DisplayLabel { get; init; } = "";
            public bool IsBuiltIn { get; init; }
            public AppThemeInfo Theme { get; init; } = null!;
        }

        private void RefreshThemePacksList()
        {
            try
            {
                ThemeService.Instance.Initialize();
                ThemeService.Instance.ReloadCustomThemes();
                var activeId = _configService.ResolveAppThemeId();
                var items = ThemeService.Instance.Themes
                    .Select(t =>
                    {
                        var active = string.Equals(t.Id, activeId, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(t.Id, ThemeService.Instance.CurrentThemeId, StringComparison.OrdinalIgnoreCase);
                        var kind = t.IsBuiltIn ? "built-in" : "custom";
                        return new ThemeListItem
                        {
                            Id = t.Id,
                            DisplayName = t.DisplayName,
                            DisplayLabel = active
                                ? $"{t.DisplayName} ({kind}) · active"
                                : $"{t.DisplayName} ({kind})",
                            IsBuiltIn = t.IsBuiltIn,
                            Theme = t
                        };
                    })
                    .ToList();
                if (ThemePacksListBox != null)
                {
                    ThemePacksListBox.ItemsSource = items;
                }

                _suppressAppThemeComboChange = true;
                try
                {
                    if (AppThemeComboBox != null)
                    {
                        AppThemeComboBox.ItemsSource = ThemeService.Instance.Themes.ToList();
                        AppThemeComboBox.SelectedItem = ThemeService.Instance.FindTheme(activeId)
                                                       ?? ThemeService.Instance.Themes.FirstOrDefault();
                    }
                }
                finally
                {
                    _suppressAppThemeComboChange = false;
                }

                if (ThemePackStatusText != null)
                {
                    ThemePackStatusText.Text =
                        $"Active: {ThemeService.Instance.CurrentThemeId}  ·  Guide: {ThemePackStore.GuideFileName}  ·  Folder: {ThemePackStore.Instance.ThemesDirectory}  ·  {items.Count} theme(s)";
                }
            }
            catch (Exception ex)
            {
                if (ThemePackStatusText != null)
                {
                    ThemePackStatusText.Text = "Failed to load themes: " + ex.Message;
                }
            }
        }

        private void ApplySelectedAppTheme(string? themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId))
            {
                return;
            }

            ThemeService.Instance.ApplyTheme(themeId);
            _configService.SetAppThemeId(themeId);
            RefreshThemePacksList();
        }

        private void AppThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAppThemeComboChange)
            {
                return;
            }

            if (AppThemeComboBox?.SelectedItem is AppThemeInfo info)
            {
                ApplySelectedAppTheme(info.Id);
            }
        }

        private void ApplyAppTheme_Click(object sender, RoutedEventArgs e)
        {
            var themeId = AppThemeComboBox?.SelectedItem is AppThemeInfo info
                ? info.Id
                : (ThemePacksListBox?.SelectedItem as ThemeListItem)?.Id;
            ApplySelectedAppTheme(themeId);
        }

        private void ThemePacksListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ThemePacksListBox?.SelectedItem is ThemeListItem item)
            {
                ApplySelectedAppTheme(item.Id);
            }
        }

        private void ImportTheme_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import theme pack",
                Filter = "Theme pack (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                var imported = ThemeService.Instance.ImportThemeFile(dialog.FileName);
                RefreshThemePacksList();
                ModernMessageBox.Show(
                    $"Imported theme \"{imported.DisplayName}\". Select it above and click Apply to use app-wide.",
                    "Theme imported",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Import theme failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportTheme_Click(object sender, RoutedEventArgs e)
        {
            if (ThemePacksListBox?.SelectedItem is not ThemeListItem item)
            {
                ModernMessageBox.Show("Select a theme to export.", "Export theme", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "Export Deploy theme pack",
                Filter = "Theme pack (*.json)|*.json",
                FileName = item.DisplayName.Replace(' ', '-') + ".json",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                var pack = ThemeService.Instance.GetExportPack(item.Id);
                ThemePackStore.Instance.ExportTheme(pack, dialog.FileName);
                ModernMessageBox.Show("Theme exported.", "Export theme", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Export theme failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteTheme_Click(object sender, RoutedEventArgs e)
        {
            if (ThemePacksListBox?.SelectedItem is not ThemeListItem item)
            {
                ModernMessageBox.Show("Select a custom theme to delete.", "Delete theme", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (item.IsBuiltIn)
            {
                ModernMessageBox.Show("Built-in themes cannot be deleted.", "Delete theme", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                $"Delete custom theme \"{item.DisplayName}\"?",
                "Delete theme",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                ThemeService.Instance.DeleteCustomTheme(item.Id);
                _configService.SetAppThemeId(ThemeService.Instance.CurrentThemeId);
                RefreshThemePacksList();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Delete theme failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThemeService.Instance.Initialize();
                var dir = ThemePackStore.Instance.ThemesDirectory;
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Open folder failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenThemeGuide_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThemeService.Instance.Initialize();
                ThemePackStore.Instance.EnsureSeeded();
                var path = ThemePackStore.Instance.GuidePath;
                if (!File.Exists(path))
                {
                    var bundled = Path.Combine(AppContext.BaseDirectory, "Themes", "Packs", ThemePackStore.GuideFileName);
                    path = File.Exists(bundled) ? bundled : path;
                }

                if (!File.Exists(path))
                {
                    ModernMessageBox.Show(
                        "Theme guide was not found. Use Open folder and look for THEME_PACK_GUIDE.md.",
                        "Open guide",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "Open guide failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshThemes_Click(object sender, RoutedEventArgs e)
            => RefreshThemePacksList();

        private void RefreshUpdateStatus(ConfigurationService.GlobalConfig? globalConfig = null)
        {
            globalConfig ??= _configService.LoadGlobalConfig();
            if (UpdateVersionText != null)
            {
                UpdateVersionText.Text = $"Current version: {new AppUpdateService().GetCurrentVersion()}";
            }

            if (UpdateLastCheckText != null)
            {
                UpdateLastCheckText.Text = globalConfig.LastUpdateCheckUtc.HasValue
                    ? $"Last automatic check: {globalConfig.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : "Last automatic check: never";
            }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            try
            {
                var owner = Window.GetWindow(this);
                await AppUpdateCoordinator.RunManualCheckAsync(owner);
                RefreshUpdateStatus();
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }

        private async Task ReloadSettingsForPath(string path)
        {
            LocalPathTextBox.Text = path;
            var projectConfig = _configService.LoadProjectConfig(path);
            LoadFtpAssignmentDraft(projectConfig);

            AutoInitGitCheckBox.IsChecked = projectConfig.AutoInitGit;
            AutoCommitCheckBox.IsChecked = projectConfig.AutoCommit;
            AutoPushCheckBox.IsChecked = projectConfig.AutoPush;
            SelectProjectKind(projectConfig.ProjectKind);
            
            var gitIgnoreLines = LoadOrCreateGitIgnoreLines(path);
            ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, gitIgnoreLines);
            RefreshIgnorePatternList();
            
            GitService.SetWorkingDirectory(path);
            await LoadGitInfo(projectConfig);
            UpdateDangerZoneUi(path);
            TerminalSuggestionsPanel?.Reload(path);
        }

        private void UpdateDangerZoneUi(string? path)
        {
            if (DangerProjectPathText == null || RemoveProjectFromAppButton == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                DangerProjectPathText.Text = "No project selected.";
                RemoveProjectFromAppButton.IsEnabled = false;
                return;
            }

            DangerProjectPathText.Text = path;
            RemoveProjectFromAppButton.IsEnabled = true;
        }

        private void RemoveProjectFromAppButton_Click(object sender, RoutedEventArgs e)
        {
            var path = (LocalPathTextBox?.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                ModernMessageBox.Show("No project is selected.", "Danger Zone", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = ModernMessageBox.Show(
                $"Remove this project from GitDeploy Pro?\n\n{path}\n\nFiles on disk will NOT be deleted. You can open the folder again later.",
                "Remove project from app",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (!confirm)
            {
                return;
            }

            try
            {
                var nextPath = _configService.RemoveRecentProject(path);
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ApplyProjectListChange(nextPath);
                }

                if (!string.IsNullOrWhiteSpace(nextPath) && Directory.Exists(nextPath))
                {
                    _ = ReloadSettingsForPath(nextPath);
                }
                else
                {
                    LocalPathTextBox.Text = string.Empty;
                    UpdateDangerZoneUi(null);
                    ClearFtpAssignmentDraft();
                }

                ModernMessageBox.Show(
                    "Project removed from the app. Disk files were kept.",
                    "Danger Zone",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not remove project:\n{ex.Message}", "Danger Zone", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadGitInfo(ProjectConfig config)
        {
            try
            {
                if (_gitService.IsGitRepository())
                {
                    string remoteUrl = await _gitService.GetRemoteUrlAsync();
                    if (string.IsNullOrWhiteSpace(remoteUrl))
                    {
                        remoteUrl = config.GitRemoteUrl?.Trim() ?? string.Empty;
                    }
                    RemoteUrlTextBox.Text = remoteUrl;

                    var branches = await _gitService.GetBranchesAsync();
                    DefaultSourceBranchComboBox.ItemsSource = branches;
                    DefaultTargetBranchComboBox.ItemsSource = branches;

                    if (branches.Contains(config.DefaultSourceBranch))
                        DefaultSourceBranchComboBox.SelectedItem = config.DefaultSourceBranch;
                    else if (branches.Any())
                        DefaultSourceBranchComboBox.SelectedIndex = 0;

                    var selectedSource = DefaultSourceBranchComboBox.SelectedItem as string ?? config.DefaultSourceBranch;
                    var targetCandidate = ResolveDistinctTargetBranch(selectedSource, config.DefaultTargetBranch, branches);
                    if (branches.Contains(targetCandidate))
                    {
                        DefaultTargetBranchComboBox.SelectedItem = targetCandidate;
                    }
                    else
                    {
                        var firstDistinct = branches.FirstOrDefault(b => !string.Equals(b, selectedSource, StringComparison.OrdinalIgnoreCase));
                        DefaultTargetBranchComboBox.SelectedItem = firstDistinct;
                    }

                    var branchStatus = await _gitService.GetBranchStatusAsync();
                    UpdateGitPushStatusBadge(branchStatus);
                }
                else
                {
                    DefaultSourceBranchComboBox.ItemsSource = null;
                    DefaultTargetBranchComboBox.ItemsSource = null;
                    RemoteUrlTextBox.Text = config.GitRemoteUrl?.Trim() ?? string.Empty;
                    UpdateGitPushStatusBadge(new BranchStatusInfo());
                }
            }
            catch { }
        }

        private void LoadFtpAssignmentDraft(ProjectConfig projectConfig)
        {
            _remoteFtpProfiles = _configService.LoadConnections()
                .Where(ConnectionProfileFilters.IsRemoteFileProfile)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _draftAssignedFtpIds.Clear();
            _draftAssignedFtpIds.AddRange(ProjectFtpAssignments.GetAssignedIds(projectConfig));
            _draftAssignedFtpIds.RemoveAll(id => !_remoteFtpProfiles.Any(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
            _draftDefaultFtpId = ProjectFtpAssignments.GetDefaultId(projectConfig);
            if (!_draftAssignedFtpIds.Any(id => string.Equals(id, _draftDefaultFtpId, StringComparison.OrdinalIgnoreCase)))
            {
                _draftDefaultFtpId = _draftAssignedFtpIds.FirstOrDefault() ?? string.Empty;
            }

            _draftFtpConfirmed = _draftAssignedFtpIds.Count <= 1 || projectConfig.FtpSyncTargetConfirmed;
            RefreshAssignedFtpUi();
        }

        private void ClearFtpAssignmentDraft()
        {
            _draftAssignedFtpIds.Clear();
            _draftDefaultFtpId = string.Empty;
            _draftFtpConfirmed = true;
            _remoteFtpProfiles = _configService.LoadConnections()
                .Where(ConnectionProfileFilters.IsRemoteFileProfile)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RefreshAssignedFtpUi();
            ClearPreview();
        }

        private ProjectConfig BuildFtpDraftConfig()
        {
            return new ProjectConfig
            {
                ConnectionProfileId = _draftDefaultFtpId,
                ConnectionProfileIds = _draftAssignedFtpIds.ToList(),
                FtpSyncTargetConfirmed = _draftFtpConfirmed
            };
        }

        private void RefreshAssignedFtpUi(string? selectId = null)
        {
            var items = _draftAssignedFtpIds
                .Select(id => _remoteFtpProfiles.FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p != null)
                .Select(p => new AssignedFtpItem(p!, string.Equals(p!.Id, _draftDefaultFtpId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (AssignedFtpProfilesList != null)
            {
                AssignedFtpProfilesList.ItemsSource = items;
                var keepId = selectId ?? _draftDefaultFtpId;
                var selected = items.FirstOrDefault(i =>
                    string.Equals(i.Profile.Id, keepId, StringComparison.OrdinalIgnoreCase));
                AssignedFtpProfilesList.SelectedItem = selected ?? items.FirstOrDefault();
            }

            if (AssignedFtpEmptyText != null)
            {
                AssignedFtpEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AddFtpProfileComboBox != null)
            {
                var available = _remoteFtpProfiles
                    .Where(p => !_draftAssignedFtpIds.Any(id =>
                        string.Equals(id, p.Id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                AddFtpProfileComboBox.ItemsSource = available;
                AddFtpProfileComboBox.SelectedItem = available.FirstOrDefault();
                if (AddFtpProfileButton != null)
                {
                    AddFtpProfileButton.IsEnabled = available.Count > 0;
                }
            }

            var preview = (AssignedFtpProfilesList?.SelectedItem as AssignedFtpItem)?.Profile
                          ?? items.FirstOrDefault()?.Profile;
            if (preview != null)
            {
                UpdatePreview(preview);
            }
            else
            {
                ClearPreview();
            }
        }

        private void AssignedFtpProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AssignedFtpProfilesList.SelectedItem is AssignedFtpItem item)
            {
                UpdatePreview(item.Profile);
            }
        }

        private void AddFtpProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddFtpProfileComboBox.SelectedItem is not ConnectionProfile profile)
            {
                return;
            }

            var draft = BuildFtpDraftConfig();
            ProjectFtpAssignments.Add(draft, profile.Id);
            _draftAssignedFtpIds.Clear();
            _draftAssignedFtpIds.AddRange(ProjectFtpAssignments.GetAssignedIds(draft));
            _draftDefaultFtpId = ProjectFtpAssignments.GetDefaultId(draft);
            _draftFtpConfirmed = draft.FtpSyncTargetConfirmed;
            RefreshAssignedFtpUi(profile.Id);
        }

        private void SetDefaultFtpButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not System.Windows.Controls.Button { DataContext: AssignedFtpItem item })
            {
                return;
            }

            var draft = BuildFtpDraftConfig();
            ProjectFtpAssignments.SetDefault(draft, item.Profile.Id, confirmed: true);
            _draftAssignedFtpIds.Clear();
            _draftAssignedFtpIds.AddRange(ProjectFtpAssignments.GetAssignedIds(draft));
            _draftDefaultFtpId = ProjectFtpAssignments.GetDefaultId(draft);
            _draftFtpConfirmed = draft.FtpSyncTargetConfirmed;
            RefreshAssignedFtpUi(item.Profile.Id);
        }

        private void RemoveAssignedFtpButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not System.Windows.Controls.Button { DataContext: AssignedFtpItem item })
            {
                return;
            }

            var draft = BuildFtpDraftConfig();
            ProjectFtpAssignments.Remove(draft, item.Profile.Id);
            _draftAssignedFtpIds.Clear();
            _draftAssignedFtpIds.AddRange(ProjectFtpAssignments.GetAssignedIds(draft));
            _draftDefaultFtpId = ProjectFtpAssignments.GetDefaultId(draft);
            _draftFtpConfirmed = draft.FtpSyncTargetConfirmed;
            RefreshAssignedFtpUi();
        }

        private void ClearPreview()
        {
            PreviewHostText.Text = "-";
            PreviewProtocolText.Text = "-";
            PreviewUserText.Text = "-";
            PreviewPathText.Text = "-";
        }

        private void UpdatePreview(ConnectionProfile p)
        {
            PreviewHostText.Text = $"{p.Host}:{p.Port}";
            PreviewProtocolText.Text = p.UseSSH ? "SFTP (SSH)" : "FTP";
            PreviewUserText.Text = p.Username;
            PreviewPathText.Text = p.RemotePath;
        }

        private void ManageConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manager = new ConnectionManagerWindow(_draftDefaultFtpId);
                WindowOwnerService.ShowDialogOwned(manager, this);

                var previousSelectedId = (AssignedFtpProfilesList.SelectedItem as AssignedFtpItem)?.Profile.Id;
                _remoteFtpProfiles = _configService.LoadConnections()
                    .Where(ConnectionProfileFilters.IsRemoteFileProfile)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _draftAssignedFtpIds.RemoveAll(id => !_remoteFtpProfiles.Any(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
                if (!_draftAssignedFtpIds.Any(id => string.Equals(id, _draftDefaultFtpId, StringComparison.OrdinalIgnoreCase)))
                {
                    _draftDefaultFtpId = _draftAssignedFtpIds.FirstOrDefault() ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(manager.SelectedProfile?.Id)
                    && ConnectionProfileFilters.IsRemoteFileProfile(manager.SelectedProfile)
                    && !_draftAssignedFtpIds.Any(id =>
                        string.Equals(id, manager.SelectedProfile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var draft = BuildFtpDraftConfig();
                    ProjectFtpAssignments.Add(draft, manager.SelectedProfile.Id);
                    _draftAssignedFtpIds.Clear();
                    _draftAssignedFtpIds.AddRange(ProjectFtpAssignments.GetAssignedIds(draft));
                    _draftDefaultFtpId = ProjectFtpAssignments.GetDefaultId(draft);
                    _draftFtpConfirmed = draft.FtpSyncTargetConfirmed;
                    previousSelectedId = manager.SelectedProfile.Id;
                }

                RefreshAssignedFtpUi(previousSelectedId);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open Connection Manager:\n{ex.ToString()}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void BrowseSshKeyButton_Click(object sender, RoutedEventArgs e)
        {
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select SSH private key",
                CheckFileExists = true,
                Filter = "SSH private key|id_*;*.pem;*.key|All files|*.*"
            };
            if (Directory.Exists(sshDir))
            {
                dialog.InitialDirectory = sshDir;
            }

            if (dialog.ShowDialog() == true)
            {
                SshKeyPathTextBox.Text = dialog.FileName;
                new SshAgentService().RememberKeyPath(dialog.FileName);
            }
        }

        private async void TestSshKeyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var remoteUrl = RemoteUrlTextBox?.Text?.Trim() ?? string.Empty;
                var keyPath = SshKeyPathTextBox?.Text?.Trim() ?? string.Empty;
                var ssh = new SshAgentService();
                if (!string.IsNullOrWhiteSpace(keyPath))
                {
                    ssh.RememberKeyPath(keyPath);
                }

                var host = SshAgentService.ResolveSshHost(remoteUrl);
                var result = await ssh.TestHostAsync(host, keyPath);
                if (result.IsReady)
                {
                    ModernMessageBox.Show(result.Message, "SSH test", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var detail = string.IsNullOrWhiteSpace(result.Details) ? result.Message : $"{result.Message}\n\n{result.Details}";
                ModernMessageBox.Show(detail, "SSH test", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, "SSH test", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenSshKeyHelperButton_Click(object sender, RoutedEventArgs e)
        {
            var remoteUrl = RemoteUrlTextBox?.Text?.Trim() ?? string.Empty;
            if (SshKeySetupWindow.ShowForRemote(this, remoteUrl))
            {
                var config = _configService.LoadGlobalConfig();
                SshKeyPathTextBox.Text = config.DefaultSshKeyPath ?? string.Empty;
            }
        }

        private async Task InitializeGitWithSshHelpAsync(List<string> branches, string remoteUrl)
        {
            if (SshAgentService.LooksLikeSshRemote(remoteUrl))
            {
                var status = await _gitService.PrepareSshForRemoteAsync(remoteUrl);
                if (!status.IsReady && !SshKeySetupWindow.ShowForRemote(this, remoteUrl))
                {
                    throw new InvalidOperationException(status.Message);
                }
            }

            try
            {
                await _gitService.InitRepoAsync(branches, remoteUrl);
            }
            catch (Exception ex) when (IsSshSetupException(ex))
            {
                if (!SshKeySetupWindow.ShowForRemote(this, remoteUrl))
                {
                    throw;
                }

                await _gitService.PushAsync();
            }
        }

        private async Task PushWithSshHelpAsync(string remoteUrl)
        {
            try
            {
                await _gitService.PushAsync();
            }
            catch (Exception ex) when (IsSshSetupException(ex))
            {
                if (!SshKeySetupWindow.ShowForRemote(this, remoteUrl))
                {
                    throw;
                }

                await _gitService.PushAsync();
            }
        }

        private static bool IsSshSetupException(Exception ex)
        {
            if (ex is GitSshSetupRequiredException)
            {
                return true;
            }

            if (ex is GitCommandException gitEx)
            {
                return SshAgentService.LooksLikeSshAuthFailure(gitEx.GetDetailedMessage());
            }

            return false;
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string projectPath = LocalPathTextBox.Text;
                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    ModernMessageBox.Show("Please select a valid Local Project Path.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(projectPath))
                {
                     ModernMessageBox.Show("Local Project Path does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                }

                var selectedProfile = _remoteFtpProfiles.FirstOrDefault(p =>
                    string.Equals(p.Id, _draftDefaultFtpId, StringComparison.OrdinalIgnoreCase));
                var ignoreEntries = GetIgnoreEntriesFromTextBox();
                string sourceBranch = DefaultSourceBranchComboBox.SelectedItem as string ?? "master";
                var availableBranches = DefaultSourceBranchComboBox.Items.OfType<string>().ToList();
                if (availableBranches.Count == 0)
                {
                    availableBranches = DefaultTargetBranchComboBox.Items.OfType<string>().ToList();
                }

                string requestedTarget = DefaultTargetBranchComboBox.SelectedItem as string ?? "";
                string targetBranch = ResolveDistinctTargetBranch(sourceBranch, requestedTarget, availableBranches);

                var projectConfig = _configService.LoadProjectConfig(projectPath);
                projectConfig.LocalProjectPath = projectPath;
                projectConfig.ConnectionProfileId = selectedProfile?.Id ?? "";
                projectConfig.ConnectionProfileIds = _draftAssignedFtpIds.ToList();
                projectConfig.FtpSyncTargetConfirmed = _draftAssignedFtpIds.Count <= 1 || _draftFtpConfirmed;
                projectConfig.FtpHost = selectedProfile?.Host ?? "";
                projectConfig.FtpPort = selectedProfile?.Port ?? 21;
                projectConfig.FtpUsername = selectedProfile?.Username ?? "";
                projectConfig.FtpPassword = selectedProfile?.Password ?? "";
                projectConfig.UseSSH = selectedProfile?.UseSSH ?? false;
                projectConfig.RemotePath = selectedProfile?.RemotePath ?? "/";
                projectConfig.DefaultSourceBranch = sourceBranch;
                projectConfig.DefaultTargetBranch = targetBranch;
                projectConfig.GitRemoteUrl = RemoteUrlTextBox.Text?.Trim() ?? string.Empty;
                projectConfig.AutoInitGit = AutoInitGitCheckBox.IsChecked ?? true;
                projectConfig.AutoCommit = AutoCommitCheckBox.IsChecked ?? true;
                projectConfig.AutoPush = AutoPushCheckBox.IsChecked ?? false;
                projectConfig.DeployMode = selectedProfile != null ? DeployMode.FtpDeploy : DeployMode.GitHubOnly;
                projectConfig.ProjectKind = ReadProjectKindFromCombo();
                projectConfig.ExcludePatterns = ignoreEntries.ToArray();

                _configService.SaveProjectConfig(projectConfig);
                ReplaceGitIgnoreFile(projectPath, ignoreEntries);
                RefreshIgnorePatternList();

                if (!string.Equals(requestedTarget, targetBranch, StringComparison.OrdinalIgnoreCase))
                {
                    DefaultTargetBranchComboBox.SelectedItem = targetBranch;
                    ModernMessageBox.Show(
                        $"Target branch was auto-adjusted to '{targetBranch}' because source and target cannot be the same.",
                        "Branch Auto-Fix",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                bool launchOnStartup = LaunchOnStartupCheckBox.IsChecked == true;
                bool showBackupLocalhostWarning = ShowBackupLocalhostWarningCheckBox.IsChecked != false;
                bool minimizeToTray = MinimizeToTrayCheckBox.IsChecked != false;
                var deployWorkers = 8;
                if (DeployDefaultWorkersTextBox != null
                    && int.TryParse((DeployDefaultWorkersTextBox.Text ?? string.Empty).Trim(), out var parsedWorkers))
                {
                    deployWorkers = parsedWorkers;
                }

                deployWorkers = Math.Clamp(deployWorkers, 1, 8);

                _configService.UpdateGlobalConfig(cfg =>
                {
                    cfg.LastProjectPath = projectPath;
                    cfg.LaunchOnStartup = launchOnStartup;
                    cfg.ShowBackupSchedulerLocalhostWarning = showBackupLocalhostWarning;
                    cfg.MinimizeToTray = minimizeToTray;
                    cfg.DefaultSshKeyPath = SshKeyPathTextBox?.Text?.Trim() ?? string.Empty;
                    cfg.DeployDefaultWorkers = deployWorkers;
                    PersistTelegramFields(cfg);
                    PersistCursorFields(cfg);
                    PersistCodexFields(cfg);
                });

                if (DeployDefaultWorkersTextBox != null)
                {
                    DeployDefaultWorkersTextBox.Text = deployWorkers.ToString();
                }

                _autoStartService.SetAutoStart(launchOnStartup);
                RefreshStartupAudit();
                GitDeployPro.Services.Telegram.TelegramPoller.Instance.Restart();
                
                GitService.SetWorkingDirectory(projectPath);

                if (!_gitService.IsGitRepository())
                {
                    var initWindow = new InitGitWindow(RemoteUrlTextBox.Text);
                    WindowOwnerService.ShowDialogOwned(initWindow, this);

                    if (initWindow.Confirmed)
                    {
                        try
                        {
                            await InitializeGitWithSshHelpAsync(initWindow.SelectedBranches, initWindow.RemoteUrl);
                            ModernMessageBox.Show("Git repository initialized successfully!", "Git Init", MessageBoxButton.OK, MessageBoxImage.Information);
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
                                    await PushWithSshHelpAsync(newRemote);
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

                ModernMessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string[] LoadOrCreateGitIgnoreLines(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return DefaultIgnorePatterns;

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

                return lines.ToArray();
            }
            catch
            {
                return DefaultIgnorePatterns;
            }
        }

        private List<string> GetIgnoreEntriesFromTextBox()
        {
            return (ExcludePatternsTextBox?.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private void RefreshGitIgnoreFromDisk()
        {
            var path = LocalPathTextBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || ExcludePatternsTextBox == null)
            {
                RefreshIgnorePatternList();
                return;
            }

            ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, LoadOrCreateGitIgnoreLines(path));
            RefreshIgnorePatternList();
        }

        private void RefreshIgnorePatternList()
        {
            if (IgnorePatternItemsControl == null)
            {
                return;
            }

            var lines = GetIgnoreEntriesFromTextBox();
            IgnorePatternItemsControl.ItemsSource = lines;
            if (IgnorePatternEmptyText != null)
            {
                IgnorePatternEmptyText.Visibility = lines.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void ExcludePatternsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            RefreshIgnorePatternList();
        }

        private void RemoveIgnorePattern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button)
            {
                return;
            }

            var target = (button.Tag as string)?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var remaining = GetIgnoreEntriesFromTextBox()
                .Where(line => !string.Equals(line, target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, remaining);
            RefreshIgnorePatternList();

            var projectPath = LocalPathTextBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                return;
            }

            try
            {
                ReplaceGitIgnoreFile(projectPath, remaining);
                var config = _configService.LoadProjectConfig(projectPath);
                config.ExcludePatterns = remaining.ToArray();
                _configService.SaveProjectConfig(config);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update .gitignore:\n{ex.Message}", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void WriteGitIgnoreFile(string projectPath, IEnumerable<string> entries)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return;

            try
            {
                string gitignorePath = Path.Combine(projectPath, ".gitignore");
                var output = File.Exists(gitignorePath)
                    ? File.ReadAllLines(gitignorePath).ToList()
                    : new List<string>();
                var seen = new HashSet<string>(
                    output.Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    var trimmed = entry.Trim();
                    if (seen.Add(trimmed))
                    {
                        output.Add(trimmed);
                    }
                }

                File.WriteAllLines(gitignorePath, output);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update .gitignore: {ex.Message}");
            }
        }

        private void ReplaceGitIgnoreFile(string projectPath, IEnumerable<string> entries)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                return;
            }

            string gitignorePath = Path.Combine(projectPath, ".gitignore");
            var output = (entries ?? Enumerable.Empty<string>())
                .Select(line => line?.Trim() ?? string.Empty)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            File.WriteAllLines(gitignorePath, output);
        }

        private async Task ApplyGitIgnoreRemovals(string projectPath, IEnumerable<string> entries)
        {
            if (!_gitService.IsGitRepository()) return;

            foreach (var entry in entries)
            {
                var trimmed = entry?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

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

             var existing = LoadOrCreateGitIgnoreLines(projectPath);
             var entries = new List<string>();
             if (!existing.Any(e => string.Equals(e, ".gitdeploy.config", StringComparison.OrdinalIgnoreCase)))
             {
                 entries.Add(".gitdeploy.config");
             }

             if (!existing.Any(e => string.Equals(e, ".gitdeploy.history", StringComparison.OrdinalIgnoreCase)))
             {
                 entries.Add(".gitdeploy.history");
             }

             if (entries.Count == 0)
             {
                 ModernMessageBox.Show("Config files are already in .gitignore.", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Information);
                 return;
             }

             WriteGitIgnoreFile(projectPath, entries);
             ExcludePatternsTextBox.Text = string.Join(Environment.NewLine, LoadOrCreateGitIgnoreLines(projectPath));
             RefreshIgnorePatternList();
             await ApplyGitIgnoreRemovals(projectPath, entries);
             ModernMessageBox.Show(".gitignore updated with app entries!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshStartupAuditButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshStartupAudit();
        }

        private void UseCurrentStartupButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _autoStartService.SetAutoStart(true);

                var entries = _autoStartService.GetGitDeployStartupEntries();
                var extraValueNames = entries
                    .Where(x => !string.Equals(x.ValueName, _autoStartService.PrimaryValueName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.ValueName)
                    .ToList();
                var removedDuplicates = _autoStartService.RemoveStartupEntries(extraValueNames);

                LaunchOnStartupCheckBox.IsChecked = true;
                _configService.UpdateGlobalConfig(cfg => cfg.LaunchOnStartup = true);
                RefreshStartupAudit();

                var duplicateHint = removedDuplicates > 0
                    ? $" Removed {removedDuplicates} duplicate startup entr{(removedDuplicates == 1 ? "y" : "ies")}."
                    : string.Empty;
                ModernMessageBox.Show(
                    $"Startup now points to this running version.{duplicateHint}",
                    "Startup Updated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update startup entry: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveStartupEntriesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var entries = _autoStartService.GetGitDeployStartupEntries();
                if (entries.Count == 0)
                {
                    RefreshStartupAudit();
                    ModernMessageBox.Show("No GitDeploy startup entries were found.", "Startup", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirm = ModernMessageBox.ShowWithResult(
                    $"Remove all detected GitDeploy startup entries? ({entries.Count})",
                    "Confirm Startup Cleanup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    "Remove",
                    "Cancel");
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                var removed = _autoStartService.RemoveStartupEntries(entries.Select(x => x.ValueName));
                LaunchOnStartupCheckBox.IsChecked = false;
                _configService.UpdateGlobalConfig(cfg => cfg.LaunchOnStartup = false);
                RefreshStartupAudit();

                ModernMessageBox.Show(
                    $"Removed {removed} startup entr{(removed == 1 ? "y" : "ies")}.",
                    "Startup Cleaned",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to remove startup entries: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshStartupAudit()
        {
            try
            {
                var currentExe = _autoStartService.GetCurrentExecutablePath();
                var primaryEntry = _autoStartService.GetPrimaryStartupEntry();
                var entries = _autoStartService.GetGitDeployStartupEntries();

                var hasAny = entries.Count > 0;
                var hasCurrent = entries.Any(x => ArePathsEqual(x.ExecutablePath, currentExe));
                var hasDuplicates = entries.Count > 1;

                var summary = !hasAny
                    ? "No GitDeploy startup entry found."
                    : hasCurrent && hasDuplicates
                        ? "Startup has multiple GitDeploy entries. Current version is detected, but cleanup is recommended."
                        : hasCurrent
                            ? "Startup is correctly pointing to this current version."
                            : "Startup points to another executable (likely an older portable release).";

                LaunchOnStartupCheckBox.IsChecked = hasAny;
                StartupAuditSummaryText.Text = summary;
                StartupCurrentExeText.Text = $"Current exe: {FormatPathWithVersion(currentExe)}";
                StartupPrimaryEntryText.Text = primaryEntry == null
                    ? $"Primary startup value ({_autoStartService.PrimaryValueName}): not found"
                    : $"Primary startup value ({primaryEntry.ValueName}): {FormatPathWithVersion(primaryEntry.ExecutablePath)}";

                StartupEntriesTextBox.Text = entries.Count == 0
                    ? "(none)"
                    : string.Join(Environment.NewLine, entries.Select(FormatStartupEntryLine));

                UseCurrentStartupButton.IsEnabled = !string.IsNullOrWhiteSpace(currentExe);
                RemoveStartupEntriesButton.IsEnabled = hasAny;
            }
            catch (Exception ex)
            {
                StartupAuditSummaryText.Text = $"Startup audit failed: {ex.Message}";
                StartupCurrentExeText.Text = "Current exe: -";
                StartupPrimaryEntryText.Text = "Primary startup value: -";
                StartupEntriesTextBox.Text = "(error)";
            }
        }

        private static bool ArePathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                var a = Path.GetFullPath(left).TrimEnd('\\', '/');
                var b = Path.GetFullPath(right).TrimEnd('\\', '/');
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string FormatPathValue(string? path)
        {
            return string.IsNullOrWhiteSpace(path) ? "-" : path.Trim();
        }

        private static string FormatPathWithVersion(string? executablePath)
        {
            var path = FormatPathValue(executablePath);
            var version = ResolveExecutableVersion(executablePath);
            return $"{path} (v{version})";
        }

        private static string FormatStartupEntryLine(AutoStartService.StartupEntryInfo entry)
        {
            if (entry == null)
            {
                return "-";
            }

            return $"{entry.ValueName} => {FormatPathWithVersion(entry.ExecutablePath)}";
        }

        private static string ResolveExecutableVersion(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return "unknown";
            }

            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath);
                var version = !string.IsNullOrWhiteSpace(info.ProductVersion)
                    ? info.ProductVersion
                    : info.FileVersion;
                if (string.IsNullOrWhiteSpace(version))
                {
                    return "unknown";
                }

                var plusIndex = version.IndexOf('+');
                return plusIndex > 0 ? version[..plusIndex] : version;
            }
            catch
            {
                return "unknown";
            }
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

        private static string ResolveDistinctTargetBranch(string sourceBranch, string requestedTarget, List<string> availableBranches)
        {
            string source = (sourceBranch ?? string.Empty).Trim();
            string target = (requestedTarget ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(target) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            foreach (var preferred in new[] { "production", "master", "main", "release" })
            {
                if (!string.Equals(source, preferred, StringComparison.OrdinalIgnoreCase) &&
                    availableBranches.Any(branch => string.Equals(branch, preferred, StringComparison.OrdinalIgnoreCase)))
                {
                    return preferred;
                }
            }

            var firstDistinct = availableBranches.FirstOrDefault(branch => !string.Equals(branch, source, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(firstDistinct))
            {
                return firstDistinct;
            }

            return string.IsNullOrWhiteSpace(source) ? "production" : $"{source}-deploy";
        }

        private async void ReSetupButton_Click(object sender, RoutedEventArgs e)
        {
            string path = LocalPathTextBox.Text;
            if (string.IsNullOrWhiteSpace(path)) return;

            await RunSetupWizardAsync(path);
        }

        private async void ResetAndRunSetupButton_Click(object sender, RoutedEventArgs e)
        {
            string path = LocalPathTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ModernMessageBox.Show("Please select a valid project folder first.", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool removeGit = ResetGitInSettingsCheckBox.IsChecked == true;
            bool removeConfig = ResetConfigInSettingsCheckBox.IsChecked == true;
            if (!removeGit && !removeConfig)
            {
                ModernMessageBox.Show("Select at least one reset option before running cleanup.", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProjectSetupResetPreview preview;
            try
            {
                preview = _setupResetService.BuildPreview(path, removeGit, removeConfig);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to prepare reset preview: {ex.Message}", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string targetsText = string.Join(Environment.NewLine, preview.Targets.Select(t => $"- {t}"));
            bool firstConfirm = ModernMessageBox.Show(
                $"Project folder:{Environment.NewLine}{preview.ProjectPath}{Environment.NewLine}{Environment.NewLine}Items to remove:{Environment.NewLine}{targetsText}{Environment.NewLine}{Environment.NewLine}Continue?",
                "Reinitialize Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Continue",
                "Cancel");

            if (!firstConfirm)
            {
                return;
            }

            bool secondConfirm = ModernMessageBox.Show(
                "This operation can remove local git history (.git). Do you want to continue now?",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Reset Now",
                "Cancel");

            if (!secondConfirm)
            {
                return;
            }

            if (sender is System.Windows.Controls.Button button)
            {
                button.IsEnabled = false;
                button.Content = "Resetting...";
            }

            try
            {
                var result = _setupResetService.ResetProject(path, removeGit, removeConfig);
                string removedText = result.RemovedPaths.Count == 0
                    ? "No files were removed."
                    : $"Removed:{Environment.NewLine}{string.Join(Environment.NewLine, result.RemovedPaths.Select(p => $"- {p}"))}";
                string skippedText = result.SkippedPaths.Count == 0
                    ? string.Empty
                    : $"{Environment.NewLine}{Environment.NewLine}Skipped (not found):{Environment.NewLine}{string.Join(Environment.NewLine, result.SkippedPaths.Select(p => $"- {p}"))}";

                ModernMessageBox.Show($"{removedText}{skippedText}", "Reset Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                GitService.SetWorkingDirectory(path);
                await RunSetupWizardAsync(path);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Reset failed: {ex.Message}", "Reset Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (sender is System.Windows.Controls.Button restoreButton)
                {
                    restoreButton.IsEnabled = true;
                    restoreButton.Content = "♻ Reset And Run Wizard";
                }
            }
        }

        private async Task RunSetupWizardAsync(string path)
        {
            GitService.SetWorkingDirectory(path);
            var gitService = new GitService();
            bool allowSkip = gitService.IsGitRepository() && !_configService.HasProjectConfigFile(path);
            var wizard = new ProjectSetupWizard(path, allowSkip);

            if (WindowOwnerService.ShowDialogOwned(wizard, this) == true)
            {
                ModernMessageBox.Show("Project configuration re-setup successfully! 🔄", "Setup Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                await ReloadSettingsForPath(path);
            }
        }

        private void LoadTelegramSettings(ConfigurationService.GlobalConfig globalConfig)
        {
            _telegramTokenDirty = false;
            if (TelegramEnabledCheckBox != null)
            {
                TelegramEnabledCheckBox.IsChecked = globalConfig.TelegramEnabled;
            }

            if (TelegramAllowedIdsTextBox != null)
            {
                TelegramAllowedIdsTextBox.Text = globalConfig.TelegramAllowedUserIds ?? string.Empty;
            }

            if (TelegramTokenBox != null)
            {
                TelegramTokenBox.Password = string.Empty;
            }

            if (TelegramTokenHint != null)
            {
                var hasToken = !string.IsNullOrWhiteSpace(globalConfig.TelegramBotToken);
                TelegramTokenHint.Text = hasToken
                    ? Loc.T("telegram.tokenSavedHint")
                    : Loc.T("telegram.tokenHint");
            }

            if (TelegramStatusText != null)
            {
                TelegramStatusText.Text = GitDeployPro.Services.Telegram.TelegramPoller.Instance.StatusText;
            }

            if (CursorAgentEnabledCheckBox != null)
            {
                CursorAgentEnabledCheckBox.IsChecked = globalConfig.CursorAgentEnabled;
            }

            if (CursorAgentPathTextBox != null)
            {
                CursorAgentPathTextBox.Text = globalConfig.CursorAgentPath ?? string.Empty;
            }

            if (CursorAgentModelTextBox != null)
            {
                CursorAgentModelTextBox.Text = globalConfig.CursorAgentModel ?? string.Empty;
            }

            if (CursorAgentTurnTimeoutMinutesTextBox != null)
            {
                var mins = globalConfig.CursorAgentTurnTimeoutMinutes;
                if (mins <= 0)
                {
                    mins = 30;
                }

                CursorAgentTurnTimeoutMinutesTextBox.Text = mins.ToString();
            }

            if (TelegramQuietProgressCheckBox != null)
            {
                TelegramQuietProgressCheckBox.IsChecked = globalConfig.TelegramQuietProgress;
            }

            if (CursorStatusText != null)
            {
                var detected = GitDeployPro.Services.Telegram.CursorAgentBridge.Instance
                    .ResolveAgentExecutable(globalConfig.CursorAgentPath);
                CursorStatusText.Text = string.IsNullOrWhiteSpace(detected)
                    ? Loc.T("cursor.agentMissing")
                    : Loc.T("cursor.agentFound", detected);
            }

            RefreshCursorDiskSummary();
            RefreshCursorChatsList();
            LoadCursorAutoCleanUi(globalConfig);

            if (AgentEngineComboBox != null)
            {
                var engine = (globalConfig.AgentEngine ?? "cursor").Trim().ToLowerInvariant();
                foreach (ComboBoxItem item in AgentEngineComboBox.Items)
                {
                    if (string.Equals(item.Tag?.ToString(), engine, StringComparison.OrdinalIgnoreCase))
                    {
                        AgentEngineComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            if (CodexAgentEnabledCheckBox != null)
            {
                CodexAgentEnabledCheckBox.IsChecked = globalConfig.CodexAgentEnabled;
            }

            if (CodexProviderComboBox != null)
            {
                var provider = CodexProviderCatalog.Normalize(globalConfig.CodexProvider);
                foreach (ComboBoxItem item in CodexProviderComboBox.Items)
                {
                    if (string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    {
                        CodexProviderComboBox.SelectedItem = item;
                        break;
                    }
                }

                ApplyCodexProviderUiHints(provider);
            }

            if (CodexCliPathTextBox != null)
            {
                CodexCliPathTextBox.Text = globalConfig.CodexCliPath ?? string.Empty;
            }

            if (CodexAgentModelTextBox != null)
            {
                var provider = CodexProviderCatalog.Normalize(globalConfig.CodexProvider);
                CodexAgentModelTextBox.Text = string.IsNullOrWhiteSpace(globalConfig.CodexAgentModel)
                    ? CodexModelCatalog.DefaultModelFor(provider)
                    : globalConfig.CodexAgentModel;
            }

            _openRouterKeyDirty = false;
            if (OpenRouterApiKeyBox != null)
            {
                OpenRouterApiKeyBox.Password = string.Empty;
            }

            if (OpenRouterKeyHint != null)
            {
                OpenRouterKeyHint.Text = string.IsNullOrWhiteSpace(globalConfig.OpenRouterApiKey)
                    ? Loc.T("settings.openRouterKeyHint")
                    : Loc.T("telegram.tokenSavedHint");
            }

            if (CodexStatusText != null)
            {
                var detected = GitDeployPro.Services.Telegram.CodexAgentBridge.Instance
                    .ResolveCodexExecutable(globalConfig.CodexCliPath);
                CodexStatusText.Text = string.IsNullOrWhiteSpace(detected)
                    ? Loc.T("codex.agentMissing")
                    : Loc.T("cursor.agentFound", detected);
            }
        }

        private void PersistTelegramFields(ConfigurationService.GlobalConfig cfg)
        {
            cfg.TelegramEnabled = TelegramEnabledCheckBox?.IsChecked == true;
            cfg.TelegramAllowedUserIds = TelegramAllowedIdsTextBox?.Text?.Trim() ?? string.Empty;
            if (_telegramTokenDirty && !string.IsNullOrWhiteSpace(TelegramTokenBox?.Password))
            {
                cfg.TelegramBotToken = EncryptionService.Encrypt(TelegramTokenBox.Password.Trim());
            }
        }

        private void TelegramTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _telegramTokenDirty = !string.IsNullOrWhiteSpace(TelegramTokenBox?.Password);
        }

        private void OpenRouterApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _openRouterKeyDirty = !string.IsNullOrWhiteSpace(OpenRouterApiKeyBox?.Password);
        }

        private void CodexProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CodexProviderComboBox?.SelectedItem is not ComboBoxItem item
                || item.Tag is not string tag)
            {
                return;
            }

            var provider = CodexProviderCatalog.Normalize(tag);
            ApplyCodexProviderUiHints(provider);
            if (CodexAgentModelTextBox == null)
            {
                return;
            }

            var current = CodexAgentModelTextBox.Text?.Trim();
            var hit = CodexModelCatalog.Find(current);
            if (string.IsNullOrWhiteSpace(current)
                || hit == null
                || !string.Equals(hit.ProviderId, provider, StringComparison.OrdinalIgnoreCase))
            {
                CodexAgentModelTextBox.Text = CodexModelCatalog.DefaultModelFor(provider);
            }
        }

        private void ApplyCodexProviderUiHints(string providerId)
        {
            var provider = CodexProviderCatalog.Get(providerId);
            if (CodexProviderHintText != null)
            {
                CodexProviderHintText.Text = provider.Hint;
            }

            if (CodexApiKeyLabel != null)
            {
                CodexApiKeyLabel.Text = provider.RequiresApiKey
                    ? $"API key ({provider.EnvKey})"
                    : "API key (optional for local)";
            }

            if (CodexAgentModelTextBox != null)
            {
                CodexAgentModelTextBox.SetValue(
                    MahApps.Metro.Controls.TextBoxHelper.WatermarkProperty,
                    provider.DefaultModel);
            }

            if (OpenRouterApiKeyBox != null)
            {
                OpenRouterApiKeyBox.IsEnabled = true;
                OpenRouterApiKeyBox.Opacity = provider.RequiresApiKey ? 1 : 0.65;
            }

            if (OpenRouterKeyHint != null && !_openRouterKeyDirty)
            {
                OpenRouterKeyHint.Text = provider.RequiresApiKey
                    ? Loc.T("settings.codexKeyHint")
                    : Loc.T("settings.codexKeyHintLocal");
            }
        }

        private void PersistCursorFields(ConfigurationService.GlobalConfig cfg)
        {
            cfg.CursorAgentEnabled = CursorAgentEnabledCheckBox?.IsChecked == true;
            cfg.CursorAgentPath = CursorAgentPathTextBox?.Text?.Trim() ?? string.Empty;
            cfg.CursorAgentModel = CursorAgentModelTextBox?.Text?.Trim() ?? string.Empty;
            cfg.CursorAgentTurnTimeoutMinutes = ParseCursorTurnTimeoutMinutes();
            PersistCursorAutoCleanFields(cfg);
            PersistSharedAgentFields(cfg);
        }

        private int ParseCursorTurnTimeoutMinutes()
        {
            var raw = CursorAgentTurnTimeoutMinutesTextBox?.Text?.Trim() ?? string.Empty;
            if (!int.TryParse(raw, out var minutes) || minutes <= 0)
            {
                return 30;
            }

            return Math.Clamp(minutes, 5, 180);
        }

        private void PersistCursorAutoCleanFields(ConfigurationService.GlobalConfig cfg)
        {
            if (CursorAutoCleanEnabledCheck == null)
            {
                return;
            }

            cfg.CursorAutoCleanEnabled = CursorAutoCleanEnabledCheck.IsChecked == true;
            cfg.CursorAutoCleanOlderThanDays = GetCursorAutoCleanDays();
            var time = CursorAutoCleanTimeBox?.Text?.Trim() ?? "03:30";
            if (GitDeployPro.Services.Cursor.CursorAutoCleanupRunner.ParseTime(time) == null)
            {
                time = "03:30";
            }

            cfg.CursorAutoCleanTimeLocal = time;
            cfg.CursorAutoCleanPurgeOrphans = CursorAutoCleanOrphansCheck?.IsChecked != false;
            cfg.CursorAutoCleanVacuum = CursorAutoCleanVacuumCheck?.IsChecked != false;
            cfg.CursorAutoCleanDiskCache = CursorAutoCleanDiskCheck?.IsChecked != false;
            cfg.CursorAutoCleanForceQuit = CursorAutoCleanForceQuitCheck?.IsChecked == true;
        }

        private void LoadCursorAutoCleanUi(ConfigurationService.GlobalConfig cfg)
        {
            _cursorAutoCleanUiLoading = true;
            try
            {
                if (CursorAutoCleanEnabledCheck != null)
                {
                    CursorAutoCleanEnabledCheck.IsChecked = cfg.CursorAutoCleanEnabled;
                }

                if (CursorAutoCleanDaysCombo != null)
                {
                    var days = cfg.CursorAutoCleanOlderThanDays <= 0 ? 30 : cfg.CursorAutoCleanOlderThanDays;
                    ComboBoxItem? match = null;
                    foreach (ComboBoxItem item in CursorAutoCleanDaysCombo.Items)
                    {
                        if (int.TryParse(item.Tag?.ToString(), out var d) && d == days)
                        {
                            match = item;
                            break;
                        }
                    }

                    CursorAutoCleanDaysCombo.SelectedItem = match ?? CursorAutoCleanDaysCombo.Items[2];
                }

                if (CursorAutoCleanTimeBox != null)
                {
                    CursorAutoCleanTimeBox.Text = string.IsNullOrWhiteSpace(cfg.CursorAutoCleanTimeLocal)
                        ? "03:30"
                        : cfg.CursorAutoCleanTimeLocal.Trim();
                }

                if (CursorAutoCleanOrphansCheck != null)
                {
                    CursorAutoCleanOrphansCheck.IsChecked = cfg.CursorAutoCleanPurgeOrphans;
                }

                if (CursorAutoCleanVacuumCheck != null)
                {
                    CursorAutoCleanVacuumCheck.IsChecked = cfg.CursorAutoCleanVacuum;
                }

                if (CursorAutoCleanDiskCheck != null)
                {
                    CursorAutoCleanDiskCheck.IsChecked = cfg.CursorAutoCleanDiskCache;
                }

                if (CursorAutoCleanForceQuitCheck != null)
                {
                    CursorAutoCleanForceQuitCheck.IsChecked = cfg.CursorAutoCleanForceQuit;
                }

                RefreshCursorAutoCleanStatus(cfg);
            }
            finally
            {
                _cursorAutoCleanUiLoading = false;
            }
        }

        private void RefreshCursorAutoCleanStatus(ConfigurationService.GlobalConfig? cfg = null)
        {
            if (CursorAutoCleanStatusText == null)
            {
                return;
            }

            cfg ??= _configService.LoadGlobalConfig();
            if (!cfg.CursorAutoCleanEnabled)
            {
                CursorAutoCleanStatusText.Text = Loc.T("cursor.autoOff");
                return;
            }

            var next = GitDeployPro.Services.Cursor.CursorAutoCleanupRunner.GetNextRunLocal(cfg);
            var last = cfg.CursorAutoCleanLastRunUtc is DateTime utc
                ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "—";
            var result = string.IsNullOrWhiteSpace(cfg.CursorAutoCleanLastResult)
                ? ""
                : " · " + cfg.CursorAutoCleanLastResult;
            CursorAutoCleanStatusText.Text = Loc.T(
                "cursor.autoStatus",
                next.ToString("yyyy-MM-dd HH:mm"),
                last) + result;
        }

        private bool _cursorAutoCleanUiLoading;

        private void CursorAutoClean_Changed(object sender, RoutedEventArgs e)
        {
            if (_cursorAutoCleanUiLoading)
            {
                return;
            }

            try
            {
                _configService.UpdateGlobalConfig(PersistCursorAutoCleanFields);
                RefreshCursorAutoCleanStatus();
                // Do NOT ForceCheck — that used to run cleanup immediately after 03:30 with no LastRun.
                GitDeployPro.Services.Cursor.CursorAutoCleanupRunner.Instance.NudgeTimer();
            }
            catch (Exception ex)
            {
                if (CursorAutoCleanStatusText != null)
                {
                    CursorAutoCleanStatusText.Text = ex.Message;
                }
            }
        }

        private async void CursorAutoCleanRunNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistCursorAutoCleanFields);
                if (CursorAutoCleanStatusText != null)
                {
                    CursorAutoCleanStatusText.Text = Loc.T("cursor.autoRunning");
                }

                if (CursorAutoCleanRunNowBtn != null)
                {
                    CursorAutoCleanRunNowBtn.IsEnabled = false;
                }

                var force = CursorAutoCleanForceQuitCheck?.IsChecked == true;
                var (ok, message) = await GitDeployPro.Services.Cursor.CursorAutoCleanupRunner.Instance
                    .RunNowAsync(force)
                    .ConfigureAwait(true);

                if (CursorAutoCleanStatusText != null)
                {
                    CursorAutoCleanStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        ok ? "Status.Success" : "Status.Error");
                    CursorAutoCleanStatusText.Text = message;
                }

                RefreshCursorChatsList();
                RefreshCursorDiskSummary();
            }
            catch (Exception ex)
            {
                if (CursorAutoCleanStatusText != null)
                {
                    CursorAutoCleanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    CursorAutoCleanStatusText.Text = ex.Message;
                }
            }
            finally
            {
                if (CursorAutoCleanRunNowBtn != null)
                {
                    CursorAutoCleanRunNowBtn.IsEnabled = true;
                }
            }
        }

        private int GetCursorAutoCleanDays()
        {
            if (CursorAutoCleanDaysCombo?.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out var days))
            {
                return days;
            }

            return 30;
        }

        private void PersistSharedAgentFields(ConfigurationService.GlobalConfig cfg)
        {
            var quiet = TelegramQuietProgressCheckBox?.IsChecked == true;
            cfg.TelegramQuietProgress = quiet;
            cfg.CursorTelegramQuietProgress = quiet; // legacy mirror
            if (AgentEngineComboBox?.SelectedItem is ComboBoxItem engineItem
                && engineItem.Tag is string engineTag
                && !string.IsNullOrWhiteSpace(engineTag))
            {
                cfg.AgentEngine = engineTag.Trim().ToLowerInvariant();
            }
        }

        private void PersistCodexFields(ConfigurationService.GlobalConfig cfg)
        {
            cfg.CodexAgentEnabled = CodexAgentEnabledCheckBox?.IsChecked == true;
            cfg.CodexCliPath = CodexCliPathTextBox?.Text?.Trim() ?? string.Empty;
            if (CodexProviderComboBox?.SelectedItem is ComboBoxItem providerItem
                && providerItem.Tag is string providerTag)
            {
                cfg.CodexProvider = CodexProviderCatalog.Normalize(providerTag);
            }

            cfg.CodexAgentModel = string.IsNullOrWhiteSpace(CodexAgentModelTextBox?.Text)
                ? CodexModelCatalog.DefaultModelFor(cfg.CodexProvider)
                : CodexAgentModelTextBox.Text.Trim();
            PersistSharedAgentFields(cfg);

            if (_openRouterKeyDirty && !string.IsNullOrWhiteSpace(OpenRouterApiKeyBox?.Password))
            {
                cfg.OpenRouterApiKey = EncryptionService.Encrypt(OpenRouterApiKeyBox.Password.Trim());
            }
        }

        private void CursorSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistCursorFields);
                if (CursorStatusText != null)
                {
                    var path = CursorAgentPathTextBox?.Text?.Trim();
                    var detected = GitDeployPro.Services.Telegram.CursorAgentBridge.Instance
                        .ResolveAgentExecutable(path);
                    CursorStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        string.IsNullOrWhiteSpace(detected) ? "Status.Error" : "Status.Success");
                    CursorStatusText.Text = string.IsNullOrWhiteSpace(detected)
                        ? Loc.T("cursor.settingsSavedMissing")
                        : Loc.T("cursor.settingsSaved", detected);
                }

                var projectPath = _configService.LoadGlobalConfig().LastProjectPath;
                GitDeployPro.Services.Telegram.AgentFacade.InvalidateAfterSettingsSave(projectPath);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CursorDetectButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcessPathFromSystem();
            var typed = CursorAgentPathTextBox?.Text?.Trim();
            var detected = GitDeployPro.Services.Telegram.CursorAgentBridge.Instance
                .ResolveAgentExecutable(typed);
            if (CursorStatusText != null)
            {
                CursorStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                    string.IsNullOrWhiteSpace(detected) ? "Status.Error" : "Status.Success");
                CursorStatusText.Text = string.IsNullOrWhiteSpace(detected)
                    ? Loc.T("cursor.agentMissing")
                    : Loc.T("cursor.agentFound", detected);
            }

            if (!string.IsNullOrWhiteSpace(detected)
                && CursorAgentPathTextBox != null
                && string.IsNullOrWhiteSpace(CursorAgentPathTextBox.Text))
            {
                CursorAgentPathTextBox.Text = detected;
            }
        }

        private void CursorDiskRefresh_Click(object sender, RoutedEventArgs e)
            => RefreshCursorDiskSummary();

        private async void CursorDiskSafeClear_Click(object sender, RoutedEventArgs e)
            => await RunCursorDiskClearAsync(GitDeployPro.Services.Cursor.CursorCleanupProfile.Safe)
                .ConfigureAwait(true);

        private async void CursorDiskAggressiveClear_Click(object sender, RoutedEventArgs e)
        {
            var confirm = ModernMessageBox.Show(
                Loc.T("cursor.diskAggressiveConfirm"),
                Loc.T("cursor.diskTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (!confirm)
            {
                return;
            }

            await RunCursorDiskClearAsync(GitDeployPro.Services.Cursor.CursorCleanupProfile.Aggressive)
                .ConfigureAwait(true);
        }

        private void CursorChatsRefresh_Click(object sender, RoutedEventArgs e)
            => RefreshCursorChatsList();

        private void CursorChatsDaysCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CursorChatsList == null)
            {
                return;
            }

            RefreshCursorChatsList();
        }

        private void CursorChatsSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (CursorChatsList?.ItemsSource is not System.Collections.IEnumerable items)
            {
                return;
            }

            var on = CursorChatsSelectAll?.IsChecked == true;
            foreach (var item in items)
            {
                if (item is CursorChatProjectVm vm)
                {
                    vm.IsSelected = on;
                }
            }
        }

        private async void CursorChatsDeleteOld_Click(object sender, RoutedEventArgs e)
        {
            var days = GetCursorChatsDaysFilter();
            var selected = (CursorChatsList?.ItemsSource as IEnumerable<CursorChatProjectVm>)
                ?.Where(x => x.IsSelected)
                .Select(x => x.WorkspaceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList() ?? new List<string>();

            if (selected.Count == 0)
            {
                ModernMessageBox.Show(
                    Loc.T("cursor.chatsNoneSelected"),
                    Loc.T("cursor.chatsTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var vacuum = CursorChatsVacuumCheck?.IsChecked == true;
            var purgeOrphans = CursorChatsPurgeOrphansCheck?.IsChecked != false;
            var confirm = ModernMessageBox.Show(
                Loc.T("cursor.chatsDeleteConfirm", days, selected.Count),
                Loc.T("cursor.chatsTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (!confirm)
            {
                return;
            }

            var scan = GitDeployPro.Services.Cursor.CursorChatCleanupService.Scan(days);
            var force = false;
            if (scan.CursorRunning)
            {
                var quit = ModernMessageBox.Show(
                    Loc.T("cursor.diskNeedQuit"),
                    Loc.T("cursor.chatsTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (!quit)
                {
                    if (CursorChatsResultText != null)
                    {
                        CursorChatsResultText.Text = Loc.T("cursor.diskCancelled");
                    }

                    return;
                }

                force = true;
            }

            SetCursorChatsBusy(true);
            if (CursorChatsResultText != null)
            {
                CursorChatsResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                CursorChatsResultText.Text = Loc.T("cursor.chatsWorking");
            }

            var progress = new Progress<GitDeployPro.Services.Cursor.CursorChatDeleteProgress>(p =>
            {
                UpdateCursorChatsProgress(p);
            });

            try
            {
                var result = await GitDeployPro.Services.Cursor.CursorChatCleanupService
                    .DeleteOlderAsync(days, selected, vacuum, force, purgeOrphans, progress)
                    .ConfigureAwait(true);

                if (CursorChatsResultText != null)
                {
                    CursorChatsResultText.Foreground = (System.Windows.Media.Brush)FindResource(
                        result.Ok ? "Status.Success" : "Status.Error");
                    CursorChatsResultText.Text = result.Message;
                }
            }
            finally
            {
                SetCursorChatsBusy(false);
            }

            RefreshCursorChatsList();
            RefreshCursorDiskSummary();
        }

        private void SetCursorChatsBusy(bool busy)
        {
            if (CursorChatsDeleteBtn != null)
            {
                CursorChatsDeleteBtn.IsEnabled = !busy;
            }

            if (CursorChatsRefreshBtn != null)
            {
                CursorChatsRefreshBtn.IsEnabled = !busy;
            }

            if (CursorChatsDaysCombo != null)
            {
                CursorChatsDaysCombo.IsEnabled = !busy;
            }

            if (CursorChatsVacuumCheck != null)
            {
                CursorChatsVacuumCheck.IsEnabled = !busy;
            }

            if (CursorChatsPurgeOrphansCheck != null)
            {
                CursorChatsPurgeOrphansCheck.IsEnabled = !busy;
            }

            if (CursorChatsList != null)
            {
                CursorChatsList.IsEnabled = !busy;
            }

            if (CursorChatsProgress != null)
            {
                CursorChatsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                if (busy)
                {
                    CursorChatsProgress.IsIndeterminate = true;
                    CursorChatsProgress.Value = 0;
                }
                else
                {
                    CursorChatsProgress.IsIndeterminate = false;
                    CursorChatsProgress.Value = 0;
                }
            }

            if (CursorChatsProgressText != null)
            {
                CursorChatsProgressText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                if (busy)
                {
                    CursorChatsProgressText.Text = Loc.T("cursor.chatsWorking");
                }
            }
        }

        private void UpdateCursorChatsProgress(GitDeployPro.Services.Cursor.CursorChatDeleteProgress p)
        {
            if (CursorChatsProgress == null || CursorChatsProgressText == null)
            {
                return;
            }

            var total = Math.Max(1, p.Total);
            var current = Math.Max(0, p.Current);
            if (p.Phase is "scan" or "vacuum" || total <= 1 && current == 0)
            {
                CursorChatsProgress.IsIndeterminate = p.Phase is "backup" or "quit" or "collect" or "auth"
                    || (p.Phase == "scan" && current == 0);
                if (!CursorChatsProgress.IsIndeterminate)
                {
                    CursorChatsProgress.Maximum = total;
                    CursorChatsProgress.Value = Math.Min(current, total);
                }
            }
            else
            {
                CursorChatsProgress.IsIndeterminate = false;
                CursorChatsProgress.Maximum = total;
                CursorChatsProgress.Value = Math.Min(current, total);
            }

            var elapsed = FormatCursorChatsElapsed(p.Elapsed);
            string text;
            if (current > 0 && p.Elapsed.TotalSeconds >= 2 && current < total)
            {
                var rate = current / p.Elapsed.TotalSeconds;
                var remainSec = rate > 0.01 ? (total - current) / rate : 0;
                var eta = FormatCursorChatsElapsed(TimeSpan.FromSeconds(Math.Max(0, remainSec)));
                text = Loc.T(
                    "cursor.chatsProgressEta",
                    string.IsNullOrWhiteSpace(p.Detail) ? p.Phase : p.Detail,
                    current,
                    total,
                    elapsed,
                    eta,
                    p.DeletedKvRows);
            }
            else
            {
                text = Loc.T(
                    "cursor.chatsProgress",
                    string.IsNullOrWhiteSpace(p.Detail) ? p.Phase : p.Detail,
                    current,
                    total,
                    elapsed,
                    p.DeletedKvRows);
            }

            CursorChatsProgressText.Text = text;
            if (CursorChatsResultText != null)
            {
                CursorChatsResultText.Text = text;
            }
        }

        private static string FormatCursorChatsElapsed(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
            }

            if (t.TotalMinutes >= 1)
            {
                return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
            }

            return $"{t.TotalSeconds:0.0}s";
        }

        private void RefreshCursorChatsList()
        {
            if (CursorChatsList == null)
            {
                return;
            }

            var days = GetCursorChatsDaysFilter();
            try
            {
                var scan = GitDeployPro.Services.Cursor.CursorChatCleanupService.Scan(days);
                if (CursorChatsSummaryText != null)
                {
                    if (!scan.Ok)
                    {
                        CursorChatsSummaryText.Text = scan.Error;
                    }
                    else
                    {
                        var fmt = GitDeployPro.Services.Cursor.CursorDiskCleanupService.FormatBytes;
                        CursorChatsSummaryText.Text = Loc.T(
                            "cursor.chatsSummary",
                            scan.TotalChats,
                            scan.OlderThanDaysTotal,
                            days,
                            fmt(scan.DatabaseBytes),
                            fmt(scan.LiveBytes),
                            fmt(scan.FreelistBytes),
                            scan.AgentBlobCount);
                        CursorChatsSummaryText.Text += "\n" + Loc.T("cursor.chatsSizeHint");
                        if (scan.CursorRunning)
                        {
                            CursorChatsSummaryText.Text += "\n" + Loc.T("cursor.chatsRunningHint");
                        }
                    }
                }

                var vms = scan.Projects.Select(p => new CursorChatProjectVm(p)).ToList();
                CursorChatsList.ItemsSource = vms;
                if (CursorChatsSelectAll != null)
                {
                    CursorChatsSelectAll.IsChecked = vms.Count > 0 && vms.All(v => v.IsSelected);
                }
            }
            catch (Exception ex)
            {
                if (CursorChatsSummaryText != null)
                {
                    CursorChatsSummaryText.Text = ex.Message;
                }

                CursorChatsList.ItemsSource = null;
            }
        }

        private int GetCursorChatsDaysFilter()
        {
            if (CursorChatsDaysCombo?.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out var days))
            {
                return days;
            }

            return 7;
        }

        private void RefreshCursorDiskSummary()
        {
            if (CursorDiskSummaryText == null)
            {
                return;
            }

            try
            {
                var scan = GitDeployPro.Services.Cursor.CursorDiskCleanupService.Scan();
                var fmt = GitDeployPro.Services.Cursor.CursorDiskCleanupService.FormatBytes;
                CursorDiskSummaryText.Text = Loc.T(
                    "cursor.diskSummary",
                    fmt(scan.TotalCursorBytes),
                    fmt(scan.StateDbBytes),
                    fmt(scan.StateBackupBytes),
                    fmt(scan.SafeReclaimableBytes));
                if (scan.CursorRunning)
                {
                    CursorDiskSummaryText.Text += "\n" + Loc.T(
                        "cursor.diskRunning",
                        string.Join(", ", scan.RunningProcessNames));
                }
            }
            catch (Exception ex)
            {
                CursorDiskSummaryText.Text = ex.Message;
            }
        }

        private async Task RunCursorDiskClearAsync(GitDeployPro.Services.Cursor.CursorCleanupProfile profile)
        {
            if (CursorDiskResultText != null)
            {
                CursorDiskResultText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                CursorDiskResultText.Text = Loc.T("cursor.diskWorking");
            }

            var scan = GitDeployPro.Services.Cursor.CursorDiskCleanupService.Scan();
            var force = false;
            if (scan.CursorRunning)
            {
                var quit = ModernMessageBox.Show(
                    Loc.T("cursor.diskNeedQuit"),
                    Loc.T("cursor.diskTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (!quit)
                {
                    if (CursorDiskResultText != null)
                    {
                        CursorDiskResultText.Text = Loc.T("cursor.diskCancelled");
                    }

                    return;
                }

                force = true;
            }

            var result = await GitDeployPro.Services.Cursor.CursorDiskCleanupService
                .ClearAsync(profile, force)
                .ConfigureAwait(true);

            if (CursorDiskResultText != null)
            {
                CursorDiskResultText.Foreground = (System.Windows.Media.Brush)FindResource(
                    result.Ok ? "Status.Success" : "Status.Error");
                CursorDiskResultText.Text = result.Message;
            }

            RefreshCursorDiskSummary();
        }

        private void CodexDetectButton_Click(object sender, RoutedEventArgs e)
        {
            GitDeployPro.Services.Telegram.CodexInstallHelper.RefreshProcessPathFromSystem();
            var typed = CodexCliPathTextBox?.Text?.Trim();
            var detected = GitDeployPro.Services.Telegram.CodexAgentBridge.Instance
                .ResolveCodexExecutable(typed);
            if (CodexStatusText != null)
            {
                CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                    string.IsNullOrWhiteSpace(detected) ? "Status.Error" : "Status.Success");
                CodexStatusText.Text = string.IsNullOrWhiteSpace(detected)
                    ? Loc.T("codex.agentMissing")
                    : Loc.T("cursor.agentFound", detected);
            }

            if (!string.IsNullOrWhiteSpace(detected)
                && CodexCliPathTextBox != null
                && string.IsNullOrWhiteSpace(CodexCliPathTextBox.Text))
            {
                CodexCliPathTextBox.Text = detected;
            }
        }

        private void CodexSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistCodexFields);
                var cfg = _configService.LoadGlobalConfig();
                var provider = CodexProviderCatalog.Get(cfg.CodexProvider);
                var hasKey = !string.IsNullOrWhiteSpace(cfg.OpenRouterApiKey);
                if (!provider.RequiresApiKey || hasKey)
                {
                    CodexConfigWriter.EnsureProviderConfig(cfg.CodexProvider, cfg.CodexAgentModel);
                }

                if (CodexStatusText != null)
                {
                    var detected = CodexAgentBridge.Instance.ResolveCodexExecutable(cfg.CodexCliPath);
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        string.IsNullOrWhiteSpace(detected) ? "Status.Error" : "Status.Success");
                    var baseMsg = (!provider.RequiresApiKey || hasKey)
                        ? Loc.T("settings.codexSaved")
                        : Loc.T("settings.codexSavedNoKey");
                    CodexStatusText.Text = string.IsNullOrWhiteSpace(detected)
                        ? baseMsg + " " + Loc.T("codex.agentMissing")
                        : baseMsg + " " + Loc.T("cursor.agentFound", detected);
                }

                if (OpenRouterKeyHint != null && hasKey)
                {
                    OpenRouterKeyHint.Text = Loc.T("telegram.tokenSavedHint");
                    _openRouterKeyDirty = false;
                    if (OpenRouterApiKeyBox != null)
                    {
                        OpenRouterApiKeyBox.Password = string.Empty;
                    }
                }

                AgentFacade.InvalidateAfterSettingsSave(cfg.LastProjectPath);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CodexTestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (CodexTestConnectionButton != null)
            {
                CodexTestConnectionButton.IsEnabled = false;
            }

            try
            {
                // Persist provider + freshly typed key before probing.
                _configService.UpdateGlobalConfig(PersistCodexFields);
                var cfgBeforeTest = _configService.LoadGlobalConfig();
                var providerBeforeTest = CodexProviderCatalog.Get(cfgBeforeTest.CodexProvider);
                if (!providerBeforeTest.RequiresApiKey
                    || !string.IsNullOrWhiteSpace(cfgBeforeTest.OpenRouterApiKey))
                {
                    CodexConfigWriter.EnsureProviderConfig(
                        cfgBeforeTest.CodexProvider,
                        cfgBeforeTest.CodexAgentModel);
                }

                if (_openRouterKeyDirty)
                {
                    _openRouterKeyDirty = false;
                    if (OpenRouterKeyHint != null)
                    {
                        OpenRouterKeyHint.Text = Loc.T("telegram.tokenSavedHint");
                    }

                    if (OpenRouterApiKeyBox != null)
                    {
                        OpenRouterApiKeyBox.Password = string.Empty;
                    }
                }

                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                    CodexStatusText.Text = Loc.T("codex.help.testRunning");
                }

                var result = await GitDeployPro.Services.Telegram.CodexAgentBridge.Instance
                    .TestConnectionAsync(null, System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);

                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        result.Ok ? "Status.Success" : "Status.Error");
                    CodexStatusText.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    CodexStatusText.Text = Loc.T("codex.help.testFail", ex.Message);
                }
            }
            finally
            {
                if (CodexTestConnectionButton != null)
                {
                    CodexTestConnectionButton.IsEnabled = true;
                }
            }
        }

        private void CursorDocsLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }

            e.Handled = true;
        }

        private static void RefreshProcessPathFromSystem()
        {
            try
            {
                var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
                var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                Environment.SetEnvironmentVariable(
                    "PATH",
                    machine + Path.PathSeparator + user,
                    EnvironmentVariableTarget.Process);
            }
            catch
            {
            }
        }

        private void CursorHelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new CursorCliHelpWindow
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CodexInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GitDeployPro.Services.Telegram.CodexInstallHelper.OpenInstallTerminal();
                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                    CodexStatusText.Text = Loc.T("codex.installTerminalOpened");
                }
            }
            catch (Exception ex)
            {
                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    CodexStatusText.Text = Loc.T("codex.installFailed", ex.Message);
                }
            }
        }

        private async void CodexUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (CodexUpdateButton != null)
            {
                CodexUpdateButton.IsEnabled = false;
            }

            try
            {
                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                    CodexStatusText.Text = Loc.T("codex.updateRunning");
                }

                var result = await GitDeployPro.Services.Telegram.CodexCliUpdateService
                    .RunManualUpdateAsync()
                    .ConfigureAwait(true);

                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                        result.Ok ? "Status.Success" : "Status.Error");
                    CodexStatusText.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                if (CodexStatusText != null)
                {
                    CodexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    CodexStatusText.Text = Loc.T("codex.updateFailed", ex.Message);
                }
            }
            finally
            {
                if (CodexUpdateButton != null)
                {
                    CodexUpdateButton.IsEnabled = true;
                }
            }
        }

        private void CodexHelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new CodexCliHelpWindow
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CursorInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenCursorCliInstallTerminal();
                if (CursorStatusText != null)
                {
                    CursorStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted");
                    CursorStatusText.Text = Loc.T("cursor.installTerminalOpened");
                }
            }
            catch (Exception ex)
            {
                if (CursorStatusText != null)
                {
                    CursorStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    CursorStatusText.Text = Loc.T("cursor.installFailed", ex.Message);
                }
            }
        }

        private static void OpenCursorCliInstallTerminal()
        {
            // Official Windows install — visible terminal so the operator can watch progress.
            // https://cursor.com/docs/cli/overview
            const string installScript = "irm 'https://cursor.com/install?win32=true' | iex";
            var inner = string.Join("; ", new[]
            {
                "Write-Host '=== GitDeploy: Installing Cursor CLI ===' -ForegroundColor Cyan",
                "Write-Host 'Command: " + installScript + "' -ForegroundColor DarkGray",
                "Write-Host ''",
                installScript,
                "Write-Host ''",
                "Write-Host '=== Install finished ===' -ForegroundColor Green",
                "Write-Host 'Close this window, then in GitDeploy click Detect agent.' -ForegroundColor Yellow",
                "Write-Host ''",
                "pause"
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -Command \"" + inner.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
        }

        private void TelegramSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistTelegramFields);
                _telegramTokenDirty = false;
                if (TelegramTokenBox != null)
                {
                    TelegramTokenBox.Password = string.Empty;
                }

                if (TelegramTokenHint != null)
                {
                    TelegramTokenHint.Text = Loc.T("telegram.tokenSavedHint");
                }

                GitDeployPro.Services.Telegram.TelegramPoller.Instance.Restart();
                if (TelegramStatusText != null)
                {
                    TelegramStatusText.Text = Loc.T("telegram.settingsSaved");
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(ex.Message, Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void TelegramTestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var typed = TelegramTokenBox?.Password;
                var result = await GitDeployPro.Services.Telegram.TelegramPoller.Instance
                    .TestConnectionAsync(typed, System.Threading.CancellationToken.None);
                if (TelegramStatusText != null)
                {
                    TelegramStatusText.Foreground = result.Ok
                        ? (System.Windows.Media.Brush)FindResource("Status.Success")
                        : (System.Windows.Media.Brush)FindResource("Status.Error");
                    TelegramStatusText.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                if (TelegramStatusText != null)
                {
                    TelegramStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Status.Error");
                    TelegramStatusText.Text = ex.Message;
                }
            }
        }

        private void LoadVpnSettings(ConfigurationService.GlobalConfig globalConfig)
        {
            if (!_vpnStatusHooked)
            {
                VpnKeepAliveService.Instance.StatusChanged += VpnKeepAlive_StatusChanged;
                _vpnStatusHooked = true;
            }

            if (VpnEnabledCheckBox != null)
            {
                VpnEnabledCheckBox.IsChecked = globalConfig.VpnEnabled;
            }

            SelectVpnProviderCombo(VpnProviderFactory.NormalizeProviderId(globalConfig.VpnProvider));
            ApplyVpnProviderUiHints();

            if (VpnGuiPathTextBox != null)
            {
                VpnGuiPathTextBox.Text = VpnProviderFactory.ResolveConfiguredPath(globalConfig);
            }

            if (VpnConnectOnStartupCheckBox != null)
            {
                VpnConnectOnStartupCheckBox.IsChecked = globalConfig.VpnConnectOnStartup;
            }

            if (VpnAutoReconnectCheckBox != null)
            {
                VpnAutoReconnectCheckBox.IsChecked = globalConfig.VpnAutoReconnect;
            }

            if (VpnHealthIntervalTextBox != null)
            {
                VpnHealthIntervalTextBox.Text = (globalConfig.VpnHealthIntervalSeconds > 0
                    ? globalConfig.VpnHealthIntervalSeconds
                    : 30).ToString();
            }

            if (VpnMaxAttemptsTextBox != null)
            {
                VpnMaxAttemptsTextBox.Text = Math.Max(0, globalConfig.VpnMaxReconnectAttempts).ToString();
            }

            if (VpnProbeHostTextBox != null)
            {
                VpnProbeHostTextBox.Text = globalConfig.VpnHealthProbeHost ?? string.Empty;
            }

            RefreshVpnProfiles(globalConfig.VpnProfileName);
            ApplyVpnStatusUi(VpnKeepAliveService.Instance.State, VpnKeepAliveService.Instance.StatusMessage);
        }

        private void PersistVpnFields(ConfigurationService.GlobalConfig cfg)
        {
            cfg.VpnEnabled = VpnEnabledCheckBox?.IsChecked == true;
            cfg.VpnProvider = VpnProviderFactory.NormalizeProviderId(GetSelectedVpnProviderId());
            var exePath = VpnGuiPathTextBox?.Text?.Trim() ?? string.Empty;
            if (cfg.VpnProvider == VpnProviderFactory.OpenVpnGui)
            {
                cfg.VpnOpenVpnGuiPath = exePath;
            }
            else
            {
                cfg.VpnOpenVpnConnectPath = exePath;
            }

            cfg.VpnProfileName = (VpnProfileCombo?.SelectedItem as VpnProfileInfo)?.Name
                                 ?? VpnProfileCombo?.SelectedValue as string
                                 ?? cfg.VpnProfileName
                                 ?? string.Empty;
            cfg.VpnConnectOnStartup = VpnConnectOnStartupCheckBox?.IsChecked == true;
            cfg.VpnAutoReconnect = VpnAutoReconnectCheckBox?.IsChecked == true;
            if (!int.TryParse(VpnHealthIntervalTextBox?.Text?.Trim(), out var interval) || interval <= 0)
            {
                interval = 30;
            }

            cfg.VpnHealthIntervalSeconds = Math.Clamp(interval, 10, 600);
            if (!int.TryParse(VpnMaxAttemptsTextBox?.Text?.Trim(), out var maxAttempts) || maxAttempts < 0)
            {
                maxAttempts = 0;
            }

            cfg.VpnMaxReconnectAttempts = maxAttempts;
            cfg.VpnHealthProbeHost = VpnProbeHostTextBox?.Text?.Trim() ?? string.Empty;
        }

        private string GetSelectedVpnProviderId()
        {
            if (VpnProviderCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag;
            }

            return VpnProviderFactory.OpenVpnConnect;
        }

        private void SelectVpnProviderCombo(string providerId)
        {
            if (VpnProviderCombo == null)
            {
                return;
            }

            _suppressVpnProviderChange = true;
            try
            {
                for (var i = 0; i < VpnProviderCombo.Items.Count; i++)
                {
                    if (VpnProviderCombo.Items[i] is ComboBoxItem item
                        && item.Tag is string tag
                        && string.Equals(tag, providerId, StringComparison.OrdinalIgnoreCase))
                    {
                        VpnProviderCombo.SelectedIndex = i;
                        return;
                    }
                }

                VpnProviderCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressVpnProviderChange = false;
            }
        }

        private void ApplyVpnProviderUiHints()
        {
            var providerId = GetSelectedVpnProviderId();
            if (VpnGuiPathTextBox != null)
            {
                MahApps.Metro.Controls.TextBoxHelper.SetWatermark(
                    VpnGuiPathTextBox,
                    providerId == VpnProviderFactory.OpenVpnGui
                        ? @"C:\Program Files\OpenVPN\bin\openvpn-gui.exe"
                        : @"C:\Program Files\OpenVPN Connect\OpenVPNConnect.exe");
            }
        }

        private void VpnProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressVpnProviderChange)
            {
                return;
            }

            ApplyVpnProviderUiHints();
            var cfg = _configService.LoadGlobalConfig();
            var providerId = GetSelectedVpnProviderId();
            if (VpnGuiPathTextBox != null)
            {
                VpnGuiPathTextBox.Text = providerId == VpnProviderFactory.OpenVpnGui
                    ? (cfg.VpnOpenVpnGuiPath ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(cfg.VpnOpenVpnConnectPath)
                        ? (cfg.VpnOpenVpnGuiPath ?? string.Empty)
                        : cfg.VpnOpenVpnConnectPath);
            }

            RefreshVpnProfiles(cfg.VpnProfileName);
        }

        private IVpnProvider GetSelectedVpnProvider() =>
            VpnProviderFactory.Create(GetSelectedVpnProviderId());

        private void RefreshVpnProfiles(string? selectedName)
        {
            if (VpnProfileCombo == null)
            {
                return;
            }

            var exePath = VpnGuiPathTextBox?.Text?.Trim();
            var profiles = GetSelectedVpnProvider().ListProfiles(exePath);
            VpnProfileCombo.ItemsSource = profiles;
            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                var match = profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, selectedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Name, Path.GetFileNameWithoutExtension(selectedName), StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    VpnProfileCombo.SelectedItem = match;
                    return;
                }
            }

            if (profiles.Count > 0 && VpnProfileCombo.SelectedItem == null)
            {
                VpnProfileCombo.SelectedIndex = 0;
            }
        }

        private void VpnKeepAlive_StatusChanged(object? sender, VpnStatusChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => ApplyVpnStatusUi(e.State, e.Message));
        }

        private void ApplyVpnStatusUi(VpnConnectionState state, string message)
        {
            if (VpnStatusText == null)
            {
                return;
            }

            var brushKey = state switch
            {
                VpnConnectionState.Connected => "Status.Success",
                VpnConnectionState.Connecting or VpnConnectionState.Reconnecting => "Status.Warning",
                VpnConnectionState.Error or VpnConnectionState.Down => "Status.Error",
                _ => "Text.Muted"
            };

            try
            {
                VpnStatusText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
            }
            catch
            {
            }

            VpnStatusText.Text = string.IsNullOrWhiteSpace(message)
                ? Loc.T("vpn.statusIdle")
                : Loc.T("vpn.statusLine", state.ToString(), message);
        }

        private void VpnDetectButton_Click(object sender, RoutedEventArgs e)
        {
            var found = GetSelectedVpnProvider().ResolveExecutablePath(VpnGuiPathTextBox?.Text);
            if (string.IsNullOrWhiteSpace(found))
            {
                ApplyVpnStatusUi(VpnConnectionState.Error, Loc.T("vpn.detectFail"));
                return;
            }

            if (VpnGuiPathTextBox != null)
            {
                VpnGuiPathTextBox.Text = found;
            }

            RefreshVpnProfiles(VpnProfileCombo?.SelectedValue as string);
            ApplyVpnStatusUi(VpnConnectionState.Idle, Loc.T("vpn.detectOk", found));
        }

        private void VpnRefreshProfilesButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = VpnProfileCombo?.SelectedValue as string
                           ?? (VpnProfileCombo?.SelectedItem as VpnProfileInfo)?.Name;
            RefreshVpnProfiles(selected);
            if (VpnProfileCombo?.Items.Count == 0)
            {
                ApplyVpnStatusUi(VpnConnectionState.Idle, Loc.T("vpn.noProfiles"));
            }
        }

        private void VpnSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistVpnFields);
                VpnKeepAliveService.Instance.ApplyConfigAndRestart();
                ApplyVpnStatusUi(VpnKeepAliveService.Instance.State, Loc.T("vpn.saved"));
            }
            catch (Exception ex)
            {
                ApplyVpnStatusUi(VpnConnectionState.Error, ex.Message);
            }
        }

        private async void VpnConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistVpnFields);
                var profile = _configService.LoadGlobalConfig().VpnProfileName;
                if (string.IsNullOrWhiteSpace(profile))
                {
                    ApplyVpnStatusUi(VpnConnectionState.Error, Loc.T("vpn.needProfile"));
                    return;
                }

                await VpnKeepAliveService.Instance.ConnectNowAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplyVpnStatusUi(VpnConnectionState.Error, ex.Message);
            }
        }

        private async void VpnDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistVpnFields);
                await VpnKeepAliveService.Instance.DisconnectNowAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplyVpnStatusUi(VpnConnectionState.Error, ex.Message);
            }
        }

        private async void VpnReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _configService.UpdateGlobalConfig(PersistVpnFields);
                await VpnKeepAliveService.Instance.ReconnectNowAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplyVpnStatusUi(VpnConnectionState.Error, ex.Message);
            }
        }

        private sealed class AssignedFtpItem
        {
            public AssignedFtpItem(ConnectionProfile profile, bool isDefault)
            {
                Profile = profile;
                IsDefault = isDefault;
            }

            public ConnectionProfile Profile { get; }
            public bool IsDefault { get; }
            public bool CanSetDefault => !IsDefault;
            public string Name => Profile.Name;
            public string Host => Profile.Host;
            public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
        }

        private sealed class CursorChatProjectVm : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isSelected;

            public CursorChatProjectVm(GitDeployPro.Services.Cursor.CursorChatProjectRow row)
            {
                WorkspaceId = row.WorkspaceId ?? string.Empty;
                DisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? "(unknown)" : row.DisplayName;
                ChatCount = row.ChatCount;
                OlderThanDaysCount = row.OlderThanDaysCount;
                NewestLocal = row.NewestUtc.HasValue
                    ? row.NewestUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "—";
                _isSelected = row.IsSelected;
            }

            public string WorkspaceId { get; }
            public string DisplayName { get; }
            public int ChatCount { get; }
            public int OlderThanDaysCount { get; }
            public string NewestLocal { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
    }
}