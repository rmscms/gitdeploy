using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Windows
{
    public partial class ProjectSetupWizard : Window
    {
        private readonly string _projectPath;
        private readonly GitService _gitService;
        private readonly ConfigurationService _configService;
        private readonly ProjectSetupResetService _resetService;
        private List<ConnectionProfile> _connections = new List<ConnectionProfile>();
        private readonly bool _gitDetectedAtStartup;
        private readonly bool _allowSkip;
        private string _lastErrorDetails = string.Empty;
        private int _progressTotal = 5;
        private int _currentStep;
        private const int StepCount = 4;
        private string _wizardLang = "en";
        private bool _suppressWizardLangChange;

        private static readonly Dictionary<string, Dictionary<string, string>> WizardStrings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["langLabel"] = "Language",
                ["title"] = "🚀 Project Setup",
                ["subtitle"] = "Configure Git and Deployment for your new project.",
                ["subtitleSkip"] = "Git is ready, but .gitdeploy.config is missing. Finish setup, or Skip to create a minimal config.",
                ["step1"] = "Step 1 of 4 — Recovery",
                ["step2"] = "Step 2 of 4 — Git repository",
                ["step3"] = "Step 3 of 4 — Deployment target",
                ["step4"] = "Step 4 of 4 — Branch strategy",
                ["tabRecovery"] = "Recovery",
                ["tabRecoveryHint"] = "Optional reset",
                ["tabGit"] = "Git",
                ["tabGitHint"] = "Repository",
                ["tabDeploy"] = "Deploy",
                ["tabDeployHint"] = "FTP / SFTP",
                ["tabBranches"] = "Branches",
                ["tabBranchesHint"] = "Source / target",
                ["recoveryTitle"] = "Recovery / Reinitialize",
                ["recoveryDesc"] = "Optional. Use this when setup is broken and you need a clean rebuild for this project folder.",
                ["resetGit"] = "Remove .git folder before setup",
                ["resetConfig"] = "Remove .gitdeploy.config before setup",
                ["resetNone"] = "No reset selected.",
                ["resetSelected"] = "Selected for cleanup: {0}",
                ["gitTitle"] = "Git Repository Source",
                ["gitDesc"] = "Choose how this project folder should connect to Git.",
                ["localGit"] = "Local Repository (Initialize new)",
                ["localGitDetected"] = "Existing Local Repository (Detected)",
                ["initialBranch"] = "Initial Branch Name:",
                ["remoteGit"] = "Remote Repository (Clone/Connect)",
                ["remoteUrl"] = "Remote URL (HTTPS/SSH):",
                ["deployTitle"] = "Deployment Target (FTP/SFTP)",
                ["deployDesc"] = "Optional. Enable FTP/SFTP if this project deploys to a remote host.",
                ["enableFtp"] = "Enable FTP Deployment",
                ["selectProfile"] = "Select Connection Profile:",
                ["manageProfiles"] = "➕ Manage Profiles",
                ["host"] = "Host:",
                ["protocol"] = "Protocol:",
                ["user"] = "User:",
                ["rootPath"] = "Root Path:",
                ["branchesTitle"] = "Branch Strategy",
                ["branchesDesc"] = "Pick development and production branches. Deploy uses the target branch.",
                ["sourceBranch"] = "Source Branch (Development):",
                ["targetBranch"] = "Target Branch (Production/Deploy):",
                ["branchesNote"] = "Files will be deployed from the Target Branch.",
                ["back"] = "← Back",
                ["skip"] = "Skip",
                ["skipTip"] = "Create a minimal .gitdeploy.config and continue without full setup",
                ["cancel"] = "Cancel",
                ["next"] = "Next →",
                ["finish"] = "Finish & Setup",
                ["needRemote"] = "Please enter a Remote URL, or choose Local Repository.",
                ["needProfile"] = "Please select a connection profile, or disable FTP Deployment.",
                ["gitSourceTitle"] = "Git Source",
                ["deployTitleShort"] = "Deployment",
            },
            ["fa"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["langLabel"] = "زبان",
                ["title"] = "🚀 راه‌اندازی پروژه",
                ["subtitle"] = "گیت و دیپلوی این پروژه را قدم‌به‌قدم تنظیم کنید.",
                ["subtitleSkip"] = "گیت آماده است، ولی .gitdeploy.config نیست. Setup را کامل کنید یا Skip بزنید تا کانفیگ مینیمال ساخته شود.",
                ["step1"] = "مرحله ۱ از ۴ — بازیابی",
                ["step2"] = "مرحله ۲ از ۴ — مخزن گیت",
                ["step3"] = "مرحله ۳ از ۴ — مقصد دیپلوی",
                ["step4"] = "مرحله ۴ از ۴ — برنچ‌ها",
                ["tabRecovery"] = "بازیابی",
                ["tabRecoveryHint"] = "ریست اختیاری",
                ["tabGit"] = "گیت",
                ["tabGitHint"] = "مخزن",
                ["tabDeploy"] = "دیپلوی",
                ["tabDeployHint"] = "FTP / SFTP",
                ["tabBranches"] = "برنچ",
                ["tabBranchesHint"] = "سورس / تارگت",
                ["recoveryTitle"] = "بازیابی / راه‌اندازی مجدد",
                ["recoveryDesc"] = "اختیاری. وقتی ستاپ خراب است و می‌خواهید این پوشه را تمیز دوباره بسازید.",
                ["resetGit"] = "حذف پوشه .git قبل از ستاپ",
                ["resetConfig"] = "حذف .gitdeploy.config قبل از ستاپ",
                ["resetNone"] = "هیچ ریستی انتخاب نشده.",
                ["resetSelected"] = "انتخاب‌شده برای پاکسازی: {0}",
                ["gitTitle"] = "منبع مخزن گیت",
                ["gitDesc"] = "نحوه اتصال این پوشه به گیت را انتخاب کنید.",
                ["localGit"] = "مخزن محلی (ایجاد جدید)",
                ["localGitDetected"] = "مخزن محلی موجود (تشخیص داده شد)",
                ["initialBranch"] = "نام برنچ اولیه:",
                ["remoteGit"] = "مخزن ریموت (کلون / اتصال)",
                ["remoteUrl"] = "آدرس ریموت (HTTPS/SSH):",
                ["deployTitle"] = "مقصد دیپلوی (FTP/SFTP)",
                ["deployDesc"] = "اختیاری. اگر پروژه به هاست ریموت دیپلوی می‌شود، FTP/SFTP را فعال کنید.",
                ["enableFtp"] = "فعال‌سازی دیپلوی FTP",
                ["selectProfile"] = "انتخاب پروفایل اتصال:",
                ["manageProfiles"] = "➕ مدیریت پروفایل‌ها",
                ["host"] = "هاست:",
                ["protocol"] = "پروتکل:",
                ["user"] = "کاربر:",
                ["rootPath"] = "مسیر ریشه:",
                ["branchesTitle"] = "استراتژی برنچ",
                ["branchesDesc"] = "برنچ توسعه و پروداکشن را مشخص کنید. دیپلوی از برنچ تارگت انجام می‌شود.",
                ["sourceBranch"] = "برنچ سورس (توسعه):",
                ["targetBranch"] = "برنچ تارگت (پروداکشن / دیپلوی):",
                ["branchesNote"] = "فایل‌ها از برنچ تارگت دیپلوی می‌شوند.",
                ["back"] = "← قبلی",
                ["skip"] = "رد کردن",
                ["skipTip"] = "ساخت .gitdeploy.config مینیمال بدون ستاپ کامل",
                ["cancel"] = "انصراف",
                ["next"] = "بعدی →",
                ["finish"] = "پایان و ستاپ",
                ["needRemote"] = "آدرس ریموت را وارد کنید، یا مخزن محلی را انتخاب کنید.",
                ["needProfile"] = "یک پروفایل اتصال انتخاب کنید، یا دیپلوی FTP را خاموش کنید.",
                ["gitSourceTitle"] = "منبع گیت",
                ["deployTitleShort"] = "دیپلوی",
            }
        };

        public bool SetupCompleted { get; private set; }

        public ProjectSetupWizard(string projectPath, bool allowSkip = false)
        {
            InitializeComponent();
            SetValue(FlowDirectionProperty, System.Windows.FlowDirection.LeftToRight);
            _projectPath = projectPath;
            _allowSkip = allowSkip;
            _gitService = new GitService();
            _configService = new ConfigurationService();
            _resetService = new ProjectSetupResetService();

            GitService.SetWorkingDirectory(_projectPath);

            _gitDetectedAtStartup = _gitService.IsGitRepository();
            if (_gitDetectedAtStartup)
            {
                LocalGitPanel.Visibility = Visibility.Collapsed;
                RemoteGitRadio.IsEnabled = false;
            }

            if (_allowSkip && _gitDetectedAtStartup)
            {
                SkipButton.Visibility = Visibility.Visible;
            }

            InitWizardLanguageCombo();
            LoadConnectionProfiles();
            ApplyWizardUiLanguage();
            UpdateResetHint();
            ShowStep(0);
        }

        private void InitWizardLanguageCombo()
        {
            _suppressWizardLangChange = true;
            try
            {
                var options = new List<LocalizationService.LanguageOption>
                {
                    new("en", "English"),
                    new("fa", "فارسی")
                };
                WizardLanguageCombo.ItemsSource = options;
                WizardLanguageCombo.DisplayMemberPath = string.Empty;
                WizardLanguageCombo.SelectedValuePath = nameof(LocalizationService.LanguageOption.Code);

                var preferred = string.Equals(LocalizationService.Instance.Language, "fa", StringComparison.OrdinalIgnoreCase)
                    ? "fa"
                    : "en";
                _wizardLang = preferred;
                WizardLanguageCombo.SelectedItem = options.First(o =>
                    string.Equals(o.Code, preferred, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressWizardLangChange = false;
            }
        }

        private void WizardLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressWizardLangChange)
            {
                return;
            }

            string? code = WizardLanguageCombo.SelectedValue as string
                           ?? (WizardLanguageCombo.SelectedItem as LocalizationService.LanguageOption)?.Code;
            if (string.IsNullOrWhiteSpace(code) || string.Equals(code, _wizardLang, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _wizardLang = code;
            SetValue(FlowDirectionProperty, System.Windows.FlowDirection.LeftToRight); // never RTL
            ApplyWizardUiLanguage();
            UpdateResetHint();
            StepCaptionText.Text = GetStepCaption(_currentStep);
            UpdateFooterForStep();
        }

        private string W(string key)
        {
            if (WizardStrings.TryGetValue(_wizardLang, out var pack) &&
                pack.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (WizardStrings.TryGetValue("en", out var en) && en.TryGetValue(key, out var enValue))
            {
                return enValue;
            }

            return key;
        }

        private string GetStepCaption(int step) => step switch
        {
            0 => W("step1"),
            1 => W("step2"),
            2 => W("step3"),
            _ => W("step4")
        };

        private void ApplyWizardUiLanguage()
        {
            SetValue(FlowDirectionProperty, System.Windows.FlowDirection.LeftToRight);

            WizardLangLabel.Text = W("langLabel");
            WizardTitleText.Text = W("title");
            WizardSubtitleText.Text = (_allowSkip && _gitDetectedAtStartup) ? W("subtitleSkip") : W("subtitle");
            StepCaptionText.Text = GetStepCaption(_currentStep);

            TabRecoveryTitle.Text = W("tabRecovery");
            TabRecoveryHint.Text = W("tabRecoveryHint");
            TabGitTitle.Text = W("tabGit");
            TabGitHint.Text = W("tabGitHint");
            TabDeployTitle.Text = W("tabDeploy");
            TabDeployHint.Text = W("tabDeployHint");
            TabBranchesTitle.Text = W("tabBranches");
            TabBranchesHint.Text = W("tabBranchesHint");

            RecoveryTitleText.Text = W("recoveryTitle");
            RecoveryDescText.Text = W("recoveryDesc");
            ResetGitFolderCheckBox.Content = W("resetGit");
            ResetConfigFileCheckBox.Content = W("resetConfig");

            GitTitleText.Text = W("gitTitle");
            GitDescText.Text = W("gitDesc");
            LocalGitRadio.Content = _gitDetectedAtStartup ? W("localGitDetected") : W("localGit");
            InitialBranchLabel.Text = W("initialBranch");
            RemoteGitRadio.Content = W("remoteGit");
            RemoteUrlLabel.Text = W("remoteUrl");

            DeployTitleText.Text = W("deployTitle");
            DeployDescText.Text = W("deployDesc");
            EnableFtpCheck.Content = W("enableFtp");
            SelectProfileLabel.Text = W("selectProfile");
            ManageProfilesButton.Content = W("manageProfiles");
            PreviewHostLabel.Text = W("host");
            PreviewProtocolLabel.Text = W("protocol");
            PreviewUserLabel.Text = W("user");
            PreviewPathLabel.Text = W("rootPath");

            BranchesTitleText.Text = W("branchesTitle");
            BranchesDescText.Text = W("branchesDesc");
            SourceBranchLabel.Text = W("sourceBranch");
            TargetBranchLabel.Text = W("targetBranch");
            BranchesFootnoteText.Text = W("branchesNote");

            BackButton.Content = W("back");
            SkipButton.Content = W("skip");
            SkipButton.ToolTip = W("skipTip");
            CancelButton.Content = W("cancel");
            NextButton.Content = W("next");
            FinishButton.Content = W("finish");
        }

        private void LoadConnectionProfiles()
        {
             _connections = _configService.LoadConnections();
             ConnectionProfileComboBox.ItemsSource = _connections;

             if (_connections.Count > 0)
             {
                 ConnectionProfileComboBox.SelectedIndex = 0;
             }
             else
             {
                 PreviewHostText.Text = "-";
                 PreviewProtocolText.Text = "-";
                 PreviewUserText.Text = "-";
                 PreviewPathText.Text = "-";
             }
        }

        private void ConnectionProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (ConnectionProfileComboBox.SelectedItem is ConnectionProfile p)
             {
                 PreviewHostText.Text = $"{p.Host}:{p.Port}";
                 PreviewProtocolText.Text = p.UseSSH ? "SFTP (SSH)" : "FTP";
                 PreviewUserText.Text = p.Username;
                 PreviewPathText.Text = p.RemotePath;
             }
        }

        private void ManageProfilesButton_Click(object sender, RoutedEventArgs e)
        {
             var manager = new ConnectionManagerWindow();
             manager.Owner = this;
             if (manager.ShowDialog() == true)
             {
                 LoadConnectionProfiles();
                 if (manager.SelectedProfile != null)
                 {
                      var selected = _connections.FirstOrDefault(c => c.Id == manager.SelectedProfile.Id);
                      if (selected != null)
                          ConnectionProfileComboBox.SelectedItem = selected;
                 }
             }
        }

        private void GitSource_Checked(object sender, RoutedEventArgs e)
        {
            if (LocalGitPanel == null || RemoteGitPanel == null) return;

            bool canReinitializeGit = !_gitDetectedAtStartup || ResetGitFolderCheckBox.IsChecked == true;

            if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = canReinitializeGit ? Visibility.Visible : Visibility.Collapsed;
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

        private void ResetOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateResetHint();

            if (!_gitDetectedAtStartup)
            {
                return;
            }

            bool gitResetRequested = ResetGitFolderCheckBox.IsChecked == true;
            RemoteGitRadio.IsEnabled = gitResetRequested;
            if (!gitResetRequested)
            {
                LocalGitRadio.IsChecked = true;
                LocalGitPanel.Visibility = Visibility.Collapsed;
            }
            else if (LocalGitRadio.IsChecked == true)
            {
                LocalGitPanel.Visibility = Visibility.Visible;
            }
        }

        private void UpdateResetHint()
        {
            bool removeGit = ResetGitFolderCheckBox?.IsChecked == true;
            bool removeConfig = ResetConfigFileCheckBox?.IsChecked == true;

            if (!removeGit && !removeConfig)
            {
                ResetHintText.Text = W("resetNone");
                return;
            }

            var targets = new List<string>();
            if (removeGit) targets.Add(".git");
            if (removeConfig) targets.Add(".gitdeploy.config");

            ResetHintText.Text = string.Format(W("resetSelected"), string.Join(", ", targets));
        }

        private bool ConfirmReinitializeReset()
        {
            bool removeGit = ResetGitFolderCheckBox.IsChecked == true;
            bool removeConfig = ResetConfigFileCheckBox.IsChecked == true;
            if (!removeGit && !removeConfig)
            {
                return true;
            }

            var preview = _resetService.BuildPreview(_projectPath, removeGit, removeConfig);
            string items = string.Join(Environment.NewLine, preview.Targets.Select(path => $"- {path}"));
            bool firstConfirm = ModernMessageBox.Show(
                $"Project folder:{Environment.NewLine}{preview.ProjectPath}{Environment.NewLine}{Environment.NewLine}Items to remove:{Environment.NewLine}{items}{Environment.NewLine}{Environment.NewLine}Continue?",
                "Reinitialize Project Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Continue",
                "Cancel");

            if (!firstConfirm)
            {
                return false;
            }

            return ModernMessageBox.Show(
                "This will remove selected setup artifacts from this project folder. Do you want to run it now?",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Reset Now",
                "Cancel");
        }

        private void ShowStep(int step)
        {
            _currentStep = Math.Clamp(step, 0, StepCount - 1);

            StepRecoveryPanel.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
            StepGitPanel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            StepDeployPanel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            StepBranchesPanel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

            StepCaptionText.Text = GetStepCaption(_currentStep);
            UpdateStepTabs();
            UpdateFooterForStep();
        }

        private void UpdateStepTabs()
        {
            RestoreTabNumbers();
            ApplyTabVisual(TabRecoveryBadge, TabRecoveryTitle, _currentStep == 0, _currentStep > 0);
            ApplyTabVisual(TabGitBadge, TabGitTitle, _currentStep == 1, _currentStep > 1);
            ApplyTabVisual(TabDeployBadge, TabDeployTitle, _currentStep == 2, _currentStep > 2);
            ApplyTabVisual(TabBranchesBadge, TabBranchesTitle, _currentStep == 3, false);
        }

        private void ApplyTabVisual(Border badge, TextBlock title, bool isActive, bool isDone)
        {
            var accent = TryFindResource("Accent.Primary") as System.Windows.Media.Brush;
            var input = TryFindResource("Surface.Input") as System.Windows.Media.Brush;
            var success = TryFindResource("Status.Success") as System.Windows.Media.Brush;
            var primaryText = TryFindResource("Text.Primary") as System.Windows.Media.Brush;
            var secondaryText = TryFindResource("Text.Secondary") as System.Windows.Media.Brush;
            var inverse = TryFindResource("Text.Inverse") as System.Windows.Media.Brush;

            if (isActive)
            {
                badge.Background = accent ?? System.Windows.Media.Brushes.DodgerBlue;
                badge.BorderThickness = new Thickness(0);
                title.Foreground = primaryText ?? System.Windows.Media.Brushes.White;
                if (badge.Child is TextBlock num)
                {
                    num.Foreground = inverse ?? System.Windows.Media.Brushes.Black;
                }
            }
            else if (isDone)
            {
                badge.Background = success ?? System.Windows.Media.Brushes.SeaGreen;
                badge.BorderThickness = new Thickness(0);
                title.Foreground = secondaryText ?? System.Windows.Media.Brushes.Gray;
                if (badge.Child is TextBlock num)
                {
                    num.Text = "✓";
                    num.Foreground = inverse ?? System.Windows.Media.Brushes.White;
                }
            }
            else
            {
                badge.Background = input ?? System.Windows.Media.Brushes.DimGray;
                badge.BorderBrush = TryFindResource("Border.Subtle") as System.Windows.Media.Brush;
                badge.BorderThickness = new Thickness(1);
                title.Foreground = secondaryText ?? System.Windows.Media.Brushes.Gray;
                if (badge.Child is TextBlock num)
                {
                    // restore step number from parent button Tag if needed — set explicitly below
                    num.Foreground = secondaryText ?? System.Windows.Media.Brushes.Gray;
                }
            }
        }

        private void RestoreTabNumbers()
        {
            if (TabRecoveryBadge.Child is TextBlock n0) n0.Text = "1";
            if (TabGitBadge.Child is TextBlock n1) n1.Text = "2";
            if (TabDeployBadge.Child is TextBlock n2) n2.Text = "3";
            if (TabBranchesBadge.Child is TextBlock n3) n3.Text = "4";
        }

        private void UpdateFooterForStep()
        {
            bool last = _currentStep >= StepCount - 1;
            BackButton.IsEnabled = _currentStep > 0;
            NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
            FinishButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StepTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag || !int.TryParse(tag, out int target))
            {
                return;
            }

            // Allow jumping backward freely; forward only one step at a time after validation.
            if (target <= _currentStep)
            {
                RestoreTabNumbers();
                ShowStep(target);
                return;
            }

            if (target == _currentStep + 1 && ValidateCurrentStep())
            {
                RestoreTabNumbers();
                ShowStep(target);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep <= 0)
            {
                return;
            }

            RestoreTabNumbers();
            ShowStep(_currentStep - 1);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateCurrentStep())
            {
                return;
            }

            if (_currentStep < StepCount - 1)
            {
                RestoreTabNumbers();
                ShowStep(_currentStep + 1);
            }
        }

        private bool ValidateCurrentStep()
        {
            if (_currentStep == 1 && RemoteGitRadio.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(RemoteUrlBox.Text))
                {
                    ModernMessageBox.Show(W("needRemote"), W("gitSourceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (_currentStep == 2 && EnableFtpCheck.IsChecked == true && ConnectionProfileComboBox.SelectedItem == null)
            {
                ModernMessageBox.Show(W("needProfile"), W("deployTitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void SetBusyState(bool isBusy)
        {
            OverlayGrid.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = !isBusy;
            FinishButton.IsEnabled = !isBusy;
            NextButton.IsEnabled = !isBusy;
            BackButton.IsEnabled = !isBusy && _currentStep > 0;
            TabRecoveryButton.IsEnabled = !isBusy;
            TabGitButton.IsEnabled = !isBusy;
            TabDeployButton.IsEnabled = !isBusy;
            TabBranchesButton.IsEnabled = !isBusy;
            if (SkipButton != null)
            {
                SkipButton.IsEnabled = !isBusy;
            }

            if (isBusy)
            {
                SetSetupProgress(0, _progressTotal, "Please wait...", indeterminate: true);
            }
        }

        private void SetSetupProgress(int current, int total, string status, bool indeterminate = false)
        {
            _progressTotal = Math.Max(1, total);
            void Apply()
            {
                SetupProgressBar.Maximum = _progressTotal;
                SetupProgressBar.IsIndeterminate = indeterminate;
                SetupProgressBar.Value = Math.Clamp(current, 0, _progressTotal);
                SetupProgressSummary.Text = indeterminate ? string.Empty : $"{Math.Clamp(current, 0, _progressTotal)}/{_progressTotal}";
                SetupProgressStatus.Text = status ?? string.Empty;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(Apply);
            }
            else
            {
                Apply();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void Skip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SkipButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                FinishButton.IsEnabled = false;
                NextButton.IsEnabled = false;
                BackButton.IsEnabled = false;
                await SaveMinimalProjectConfigAsync();
                SetupCompleted = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                SkipButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                FinishButton.IsEnabled = true;
                UpdateFooterForStep();
                ModernMessageBox.Show($"Could not create config: {ex.Message}", "Skip Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SaveMinimalProjectConfigAsync()
        {
            string sourceBranch = "master";
            string remoteUrl = string.Empty;

            try
            {
                if (_gitService.IsGitRepository())
                {
                    try
                    {
                        sourceBranch = NormalizeBranchName(await _gitService.GetCurrentBranchAsync(), "master");
                    }
                    catch
                    {
                        sourceBranch = "master";
                    }

                    try
                    {
                        remoteUrl = (await _gitService.GetRemoteUrlAsync() ?? string.Empty).Trim();
                    }
                    catch
                    {
                        remoteUrl = string.Empty;
                    }
                }
            }
            catch
            {
            }

            string targetBranch = ResolveDistinctTargetBranch(sourceBranch, "production");

            var config = new ProjectConfig
            {
                LocalProjectPath = _projectPath,
                DefaultSourceBranch = sourceBranch,
                DefaultTargetBranch = targetBranch,
                GitRemoteUrl = remoteUrl,
                AutoInitGit = true,
                AutoCommit = true,
                DeployMode = DeployMode.GitHubOnly
            };

            _configService.SaveProjectConfig(config);
        }

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrWhiteSpace(LogText.Text) || LogText.Text == "Initializing...")
                {
                    LogText.Text = message;
                }
                else
                {
                    LogText.Text += Environment.NewLine + message;
                }

                OverlayLogScrollViewer?.ScrollToEnd();
            });
        }

        private async void Finish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ConfirmReinitializeReset())
                {
                    return;
                }

                bool removeGit = ResetGitFolderCheckBox.IsChecked == true;
                bool removeConfig = ResetConfigFileCheckBox.IsChecked == true;
                bool needsGitInit = !_gitService.IsGitRepository() || removeGit;

                // Stages: optional reset, git setup, save config, commit check, done
                int totalStages = 5;
                if (!removeGit && !removeConfig)
                {
                    totalStages = 4;
                }

                SetBusyState(true);
                _lastErrorDetails = string.Empty;
                CopyErrorDetailsButton.Visibility = Visibility.Collapsed;
                LogText.Text = "Initializing...";
                SetSetupProgress(0, totalStages, "Starting setup...", indeterminate: true);

                int stage = 0;

                if (removeGit || removeConfig)
                {
                    stage++;
                    SetSetupProgress(stage, totalStages, "Cleaning project setup artifacts...");
                    AddLog("Applying project cleanup options...");
                    var resetResult = _resetService.ResetProject(_projectPath, removeGit, removeConfig);

                    if (resetResult.RemovedPaths.Count > 0)
                    {
                        AddLog($"Removed: {string.Join(", ", resetResult.RemovedPaths.Select(Path.GetFileName))}");
                    }

                    if (resetResult.SkippedPaths.Count > 0)
                    {
                        AddLog($"Skipped (not found): {string.Join(", ", resetResult.SkippedPaths.Select(Path.GetFileName))}");
                    }

                    GitService.SetWorkingDirectory(_projectPath);
                    await Task.Delay(200);
                }

                string sourceBranch = NormalizeBranchName(SourceBranchCombo.Text, "master");
                string targetBranch = ResolveDistinctTargetBranch(sourceBranch, TargetBranchCombo.Text);
                if (!string.Equals(targetBranch, TargetBranchCombo.Text?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TargetBranchCombo.Text = targetBranch;
                    AddLog($"Target branch adjusted to '{targetBranch}' to keep deploy flow stable.");
                }

                // Git Setup
                stage++;
                SetSetupProgress(stage, totalStages, needsGitInit ? "Configuring Git repository..." : "Verifying Git configuration...");
                if (!_gitService.IsGitRepository())
                {
                    if (LocalGitRadio.IsChecked == true)
                    {
                        string branch = NormalizeBranchName(LocalBranchName.Text, sourceBranch);

                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing local repository...");
                        await Task.Delay(200);

                        var initBranches = new List<string> { branch };
                        if (!string.IsNullOrEmpty(targetBranch) && !string.Equals(targetBranch, branch, StringComparison.OrdinalIgnoreCase))
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
                            SetBusyState(false);
                            return;
                        }

                        AddLog("Creating .gitignore...");
                        CreateGitIgnore(_projectPath);

                        AddLog("Initializing & Connecting to Remote...");
                        var initBranches = new List<string> { sourceBranch };
                        if (!string.IsNullOrEmpty(targetBranch) && !string.Equals(targetBranch, sourceBranch, StringComparison.OrdinalIgnoreCase))
                        {
                            initBranches.Add(targetBranch);
                        }

                        await _gitService.InitRepoAsync(initBranches, remote);

                        AddLog("Pulling changes from remote...");
                        try
                        {
                            await _gitService.PullAsync();
                        }
                        catch (Exception pullEx)
                        {
                            AddLog($"Remote pull warning: {pullEx.Message}");
                        }
                    }
                }
                else
                {
                    AddLog("Verifying Git configuration...");
                    CreateGitIgnore(_projectPath);
                }

                // Create Project Config
                stage++;
                SetSetupProgress(stage, totalStages, "Saving project configuration...");
                AddLog("Saving project configuration...");
                string detectedRemoteUrl = string.Empty;
                try
                {
                    detectedRemoteUrl = (await _gitService.GetRemoteUrlAsync()).Trim();
                }
                catch
                {
                    detectedRemoteUrl = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(detectedRemoteUrl) && RemoteGitRadio.IsChecked == true)
                {
                    detectedRemoteUrl = RemoteUrlBox.Text?.Trim() ?? string.Empty;
                }

                var config = new ProjectConfig
                {
                    LocalProjectPath = _projectPath,
                    DefaultSourceBranch = sourceBranch,
                    DefaultTargetBranch = targetBranch,
                    GitRemoteUrl = detectedRemoteUrl,

                    AutoInitGit = true,
                    AutoCommit = true,
                    DeployMode = EnableFtpCheck.IsChecked == true ? DeployMode.FtpDeploy : DeployMode.GitHubOnly
                };

                if (EnableFtpCheck.IsChecked == true)
                {
                    var selectedProfile = ConnectionProfileComboBox.SelectedItem as ConnectionProfile;
                    if (selectedProfile == null)
                    {
                         ModernMessageBox.Show("Please select a connection profile.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                         SetBusyState(false);
                         return;
                    }

                    config.ConnectionProfileId = selectedProfile.Id;
                    config.ConnectionProfileIds = new List<string> { selectedProfile.Id };
                    config.FtpSyncTargetConfirmed = true;

                    // Legacy fields backup
                    config.FtpHost = selectedProfile.Host;
                    config.FtpPort = selectedProfile.Port;
                    config.FtpUsername = selectedProfile.Username;
                    config.FtpPassword = selectedProfile.Password;
                    config.UseSSH = selectedProfile.UseSSH;
                    config.RemotePath = selectedProfile.RemotePath;
                }

                _configService.SaveProjectConfig(config);

                // Initial Commit (if needed)
                stage++;
                SetSetupProgress(stage, totalStages, "Checking for uncommitted changes...");
                AddLog("Checking for changes...");
                int pendingChanges = await _gitService.GetUncommittedCountAsync();
                if (pendingChanges > 0)
                {
                    SetSetupProgress(stage, totalStages, $"Committing {pendingChanges} change(s)...");
                    AddLog($"Committing {pendingChanges} changes...");
                    await _gitService.CommitChangesAsync("Initial setup by GitDeploy Pro");
                }

                stage = totalStages;
                SetSetupProgress(stage, totalStages, "Setup complete!");
                AddLog("Setup Complete!");
                await Task.Delay(400);

                SetupCompleted = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                SetBusyState(false);
                _lastErrorDetails = BuildErrorDetails(ex);
                CopyErrorDetailsButton.Visibility = Visibility.Visible;
                AddLog($"Setup failed: {ex.Message}");
                ModernMessageBox.Show($"Setup failed: {GetUserFacingError(ex)}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetUserFacingError(Exception ex)
        {
            if (ex is GitCommandException gitEx)
            {
                return $"{gitEx.Command} {gitEx.Arguments} failed (exit {gitEx.ExitCode}). {gitEx.GetDetailedMessage()}";
            }

            return ex.Message;
        }

        private static string BuildErrorDetails(Exception ex)
        {
            if (ex is GitCommandException gitEx)
            {
                return
                    $"Command: {gitEx.Command} {gitEx.Arguments}{Environment.NewLine}" +
                    $"Working Directory: {gitEx.WorkingDirectory}{Environment.NewLine}" +
                    $"Exit Code: {gitEx.ExitCode}{Environment.NewLine}{Environment.NewLine}" +
                    $"STDERR:{Environment.NewLine}{gitEx.StandardError}{Environment.NewLine}{Environment.NewLine}" +
                    $"STDOUT:{Environment.NewLine}{gitEx.StandardOutput}";
            }

            return ex.ToString();
        }

        private void CopyErrorDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastErrorDetails))
            {
                ModernMessageBox.Show("No error details available yet.", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(_lastErrorDetails);
                ModernMessageBox.Show("Error details copied to clipboard.", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to copy details: {ex.Message}", "Copy Details", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string NormalizeBranchName(string? branchName, string fallback)
        {
            string normalized = (branchName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = fallback;
            }

            return string.IsNullOrWhiteSpace(normalized) ? "master" : normalized;
        }

        private static string ResolveDistinctTargetBranch(string sourceBranch, string? requestedTarget)
        {
            string source = NormalizeBranchName(sourceBranch, "master");
            string candidate = NormalizeBranchName(requestedTarget, "production");

            if (!string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            foreach (var fallback in new[] { "production", "master", "main", "release" })
            {
                if (!string.Equals(source, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return fallback;
                }
            }

            return source + "-deploy";
        }

        private static readonly string[] DefaultLaravelNodeGitIgnore =
        {
            "# ------------------------------------------------------------",
            "# GitDeploy Pro: default .gitignore for Laravel + Node.js",
            "# ------------------------------------------------------------",
            "",
            "# OS / Editor",
            ".DS_Store",
            "Thumbs.db",
            ".idea/",
            ".vscode/",
            ".cursor/",
            ".vs/",
            "",
            "# Build artifacts",
            "bin/",
            "obj/",
            "dist/",
            "build/",
            "coverage/",
            "*.tsbuildinfo",
            "",
            "# Logs",
            "*.log",
            "npm-debug.log*",
            "yarn-debug.log*",
            "yarn-error.log*",
            "pnpm-debug.log*",
            "",
            "# Node.js",
            "node_modules/",
            ".npm/",
            ".pnpm-store/",
            ".yarn/",
            ".yarn/cache/",
            ".next/",
            ".nuxt/",
            ".svelte-kit/",
            "",
            "# Laravel / PHP",
            "vendor/",
            ".env",
            ".env.*",
            "!.env.example",
            ".phpunit.result.cache",
            "public/hot",
            "public/build/",
            "public/storage",
            "storage/*.key",
            "storage/logs/",
            "storage/framework/cache/",
            "storage/framework/sessions/",
            "storage/framework/testing/",
            "storage/framework/views/",
            "bootstrap/cache/*.php",
            "",
            "# GitDeploy Pro",
            ".gitdeploy.config",
            ".gitdeploy.history"
        };

        private void CreateGitIgnore(string path)
        {
            try
            {
                string ignoreFile = Path.Combine(path, ".gitignore");
                if (!File.Exists(ignoreFile))
                {
                    File.WriteAllLines(ignoreFile, DefaultLaravelNodeGitIgnore);
                }
            }
            catch { }
        }
    }
}
