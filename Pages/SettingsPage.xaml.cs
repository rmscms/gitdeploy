using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Theme;
using GitDeployPro.Services.Update;
using GitDeployPro.Windows;
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

        public SettingsPage()
        {
            InitializeComponent();
            _configService = new ConfigurationService();
            _gitService = new GitService();
            LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLanguageUiTexts();
            ShowSettingsSection("general");
            LoadSettings();
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

            if (SettingsPanelThemes != null)
            {
                SettingsPanelThemes.Visibility = section == "themes" ? Visibility.Visible : Visibility.Collapsed;
            }

            SetNavActive(NavGeneralButton, section == "general");
            SetNavActive(NavServerButton, section == "server");
            SetNavActive(NavGitButton, section == "git");
            SetNavActive(NavTerminalButton, section == "terminal");
            SetNavActive(NavThemesButton, section == "themes");

            if (SettingsSectionSubtitle != null)
            {
                SettingsSectionSubtitle.Text = section switch
                {
                    "server" => "FTP/SFTP connection profile and setup recovery.",
                    "git" => "Remote, branches, deploy automation, and ignore patterns.",
                    "terminal" => "Terminal autocomplete commands and scopes.",
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
                TerminalSuggestionsPanel?.Reload(globalConfig.LastProjectPath);
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

                var projectConfig = new ProjectConfig
                {
                    LocalProjectPath = projectPath,
                    ConnectionProfileId = selectedProfile?.Id ?? "",
                    ConnectionProfileIds = _draftAssignedFtpIds.ToList(),
                    FtpSyncTargetConfirmed = _draftAssignedFtpIds.Count <= 1 || _draftFtpConfirmed,
                    FtpHost = selectedProfile?.Host ?? "",
                    FtpPort = selectedProfile?.Port ?? 21,
                    FtpUsername = selectedProfile?.Username ?? "",
                    FtpPassword = selectedProfile?.Password ?? "",
                    UseSSH = selectedProfile?.UseSSH ?? false,
                    RemotePath = selectedProfile?.RemotePath ?? "/",
                    DefaultSourceBranch = sourceBranch,
                    DefaultTargetBranch = targetBranch,
                    GitRemoteUrl = RemoteUrlTextBox.Text?.Trim() ?? string.Empty,
                    AutoInitGit = AutoInitGitCheckBox.IsChecked ?? true,
                    AutoCommit = AutoCommitCheckBox.IsChecked ?? true,
                    AutoPush = AutoPushCheckBox.IsChecked ?? false,
                    DeployMode = selectedProfile != null ? DeployMode.FtpDeploy : DeployMode.GitHubOnly
                };
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

                _configService.UpdateGlobalConfig(cfg =>
                {
                    cfg.LastProjectPath = projectPath;
                    cfg.LaunchOnStartup = launchOnStartup;
                    cfg.ShowBackupSchedulerLocalhostWarning = showBackupLocalhostWarning;
                    cfg.MinimizeToTray = minimizeToTray;
                });

                _autoStartService.SetAutoStart(launchOnStartup);
                RefreshStartupAudit();
                
                GitService.SetWorkingDirectory(projectPath);

                if (!_gitService.IsGitRepository())
                {
                    var initWindow = new InitGitWindow(RemoteUrlTextBox.Text);
                    WindowOwnerService.ShowDialogOwned(initWindow, this);

                    if (initWindow.Confirmed)
                    {
                        try
                        {
                            await _gitService.InitRepoAsync(initWindow.SelectedBranches, initWindow.RemoteUrl);
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
    }
}