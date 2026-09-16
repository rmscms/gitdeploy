using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Telegram;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class TelegramChatPage : Page
    {
        private readonly ConfigurationService _config = new();
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly ObservableCollection<TelegramThreadItemVm> _threads = new();
        private readonly ObservableCollection<TelegramMessageItemVm> _messages = new();
        private bool _suppressThreadSelect;
        private string _openProjectPath = "";
        private string? _pendingPhotoPath;
        private string _lastAnnouncedWorkspacePath = "";

        public TelegramChatPage()
        {
            InitializeComponent();
            ThreadsList.ItemsSource = _threads;
            MessagesList.ItemsSource = _messages;
            Loaded += TelegramChatPage_Loaded;
            Unloaded += TelegramChatPage_Unloaded;
        }

        private void TelegramChatPage_Loaded(object sender, RoutedEventArgs e)
        {
            TelegramPoller.Instance.ChatPageVisible = true;
            TelegramPoller.Instance.MessageReceived += PollerOnMessageReceived;
            TelegramPoller.Instance.StatusChanged += PollerOnStatusChanged;
            TelegramPoller.Instance.ThreadCleared += PollerOnThreadCleared;
            ProjectWorkspace.CurrentProjectChanged += WorkspaceOnProjectChanged;
            LocalizationService.Instance.LanguageChanged += LanguageChanged;
            ChatStatusText.Text = TelegramPoller.Instance.StatusText;
            RefreshThreads(selectPath: ResolveInitialProjectPath());
        }

        private void TelegramChatPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TelegramPoller.Instance.ChatPageVisible = false;
            TelegramPoller.Instance.MessageReceived -= PollerOnMessageReceived;
            TelegramPoller.Instance.StatusChanged -= PollerOnStatusChanged;
            TelegramPoller.Instance.ThreadCleared -= PollerOnThreadCleared;
            ProjectWorkspace.CurrentProjectChanged -= WorkspaceOnProjectChanged;
            LocalizationService.Instance.LanguageChanged -= LanguageChanged;
        }

        private void LanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() => RefreshThreads(_openProjectPath));
        }

        private string ResolveInitialProjectPath()
        {
            var active = _store.GetActiveProjectPath();
            if (!TelegramPaths.IsUnassigned(active))
            {
                return active;
            }

            return _config.LoadGlobalConfig().LastProjectPath ?? string.Empty;
        }

        private void RefreshThreads(string? selectPath)
        {
            var recent = _config.LoadGlobalConfig().RecentProjects?
                .Select(p => p.Path)
                .ToList() ?? new();
            var summaries = _store.ListThreads(recent);
            var filter = ThreadSearchBox?.Text?.Trim() ?? string.Empty;

            _suppressThreadSelect = true;
            _threads.Clear();
            foreach (var summary in summaries)
            {
                if (!string.IsNullOrWhiteSpace(filter)
                    && summary.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                _threads.Add(new TelegramThreadItemVm(summary));
            }

            TelegramThreadItemVm? match = null;
            if (!string.IsNullOrWhiteSpace(selectPath))
            {
                match = _threads.FirstOrDefault(t =>
                    string.Equals(t.ProjectPath, selectPath, StringComparison.OrdinalIgnoreCase));
            }

            match ??= _threads.FirstOrDefault();
            ThreadsList.SelectedItem = match;
            _suppressThreadSelect = false;

            if (match != null)
            {
                OpenThread(match.ProjectPath, switchProject: false);
            }
            else
            {
                _openProjectPath = string.Empty;
                ChatTitleText.Text = Loc.T("telegram.pickThread");
                _messages.Clear();
            }
        }

        private void OpenThread(string projectPath, bool switchProject)
        {
            _openProjectPath = projectPath;
            ChatTitleText.Text = TelegramPaths.DisplayName(projectPath);
            ChatStatusText.Text = TelegramPoller.Instance.StatusText;
            _store.MarkRead(projectPath);

            var thread = _store.LoadThread(projectPath);
            _messages.Clear();
            foreach (var message in thread.Messages)
            {
                _messages.Add(new TelegramMessageItemVm(message));
            }

            ScrollMessagesToEnd();
            UpdateThreadPreview(projectPath);

            var workspace = CursorWorkspaceRoots.GetRoots(projectPath);
            if (workspace.HasMultiRoot)
            {
                ChatStatusText.Text = CursorWorkspaceRoots.FormatBadge(workspace)
                    + " · "
                    + (TelegramPoller.Instance.StatusText ?? string.Empty);
            }

            if (switchProject && !TelegramPaths.IsUnassigned(projectPath) && Directory.Exists(projectPath))
            {
                if (Window.GetWindow(this) is MainWindow main)
                {
                    main.SetCurrentProject(projectPath, showSetupWizard: false);
                }
                else
                {
                    TelegramProjectSync.ApplyToGitDeploy(projectPath);
                }
            }
        }

        private void AnnounceWorkspaceIfNeeded(string projectPath)
        {
            string full;
            try
            {
                full = Path.GetFullPath(projectPath.Trim());
            }
            catch
            {
                return;
            }

            if (string.Equals(_lastAnnouncedWorkspacePath, full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastAnnouncedWorkspacePath = full;
            CursorWorkspaceRoots.AnnounceToChatAndTelegram(full);
        }

        private void ThreadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressThreadSelect || ThreadsList.SelectedItem is not TelegramThreadItemVm item)
            {
                return;
            }

            OpenThread(item.ProjectPath, switchProject: true);
        }

        private void ThreadSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshThreads(_openProjectPath);
        }

        private void WorkspaceOnProjectChanged(object? sender, string path)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrWhiteSpace(path) || TelegramPaths.IsUnassigned(path))
                {
                    RefreshThreads(_openProjectPath);
                    return;
                }

                // Telegram (or header) switched the workspace — open that project's chat too.
                RefreshThreads(path);
                AnnounceWorkspaceIfNeeded(path);
            });
        }

        private void PollerOnMessageReceived(object? sender, TelegramMessageEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.Equals(e.ProjectPath, _openProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    _messages.Add(new TelegramMessageItemVm(e.Message));
                    _store.MarkRead(e.ProjectPath);
                    ScrollMessagesToEnd();
                }

                UpdateThreadPreview(e.ProjectPath);
            });
        }

        private void PollerOnStatusChanged(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (ChatStatusText != null)
                {
                    ChatStatusText.Text = status;
                }
            });
        }

        private void PollerOnThreadCleared(object? sender, string projectPath)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.Equals(projectPath, _openProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    _messages.Clear();
                }

                RefreshThreads(_openProjectPath);
            });
        }

        private void ClearChatButton_Click(object sender, RoutedEventArgs e)
        {
            ClearLocalChatForPath(_openProjectPath);
        }

        private void ThreadItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem row || row.DataContext is not TelegramThreadItemVm vm)
            {
                return;
            }

            ThreadsList.SelectedItem = vm;
            var menu = new ContextMenu();

            var manageItem = new MenuItem { Header = Loc.T("telegram.workspace.manageMenu") };
            manageItem.Click += (_, _) => OpenManageWorkspace(vm.ProjectPath);
            menu.Items.Add(manageItem);

            var clearItem = new MenuItem { Header = Loc.T("telegram.threadClearMenu") };
            clearItem.Click += (_, _) => ClearLocalChatForPath(vm.ProjectPath);
            menu.Items.Add(clearItem);

            row.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OpenManageWorkspace(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            if (!Directory.Exists(projectPath))
            {
                ModernMessageBox.Show(
                    Loc.T("telegram.workspace.missingProject"),
                    Loc.T("telegram.workspace.manageTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var owner = Window.GetWindow(this);
            var dialog = new ManageWorkspaceWindow(projectPath)
            {
                Owner = owner
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _lastAnnouncedWorkspacePath = Path.GetFullPath(projectPath);
                }
                catch
                {
                    _lastAnnouncedWorkspacePath = projectPath;
                }

                RefreshThreads(projectPath);
            }
        }

        private void ClearLocalChatForPath(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return;
            }

            if (!ModernMessageBox.Show(
                    Loc.T("telegram.clearLocalConfirm"),
                    Loc.T("telegram.btnClear"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question))
            {
                return;
            }

            var cleared = TelegramChatCleaner.ClearLocal(projectPath);
            ChatStatusText.Text = TelegramChatCleaner.LocalSummary(cleared);
            if (string.Equals(projectPath, _openProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                _messages.Clear();
            }

            RefreshThreads(_openProjectPath);
        }

        private async void WipeTelegramButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_openProjectPath) || TelegramPaths.IsUnassigned(_openProjectPath))
            {
                return;
            }

            if (!ModernMessageBox.Show(
                    Loc.T("telegram.wipeTelegramConfirm"),
                    Loc.T("telegram.btnWipeTelegram"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning))
            {
                return;
            }

            WipeTelegramButton.IsEnabled = false;
            ClearChatButton.IsEnabled = false;
            try
            {
                var config = _config.LoadGlobalConfig();
                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                var chatId = _store.GetLastChatId();
                if (string.IsNullOrWhiteSpace(token) || chatId == 0)
                {
                    // Still clear local even if Telegram is offline.
                    var localOnly = TelegramChatCleaner.ClearLocal(_openProjectPath);
                    ChatStatusText.Text = TelegramChatCleaner.LocalSummary(localOnly);
                    return;
                }

                ChatStatusText.Text = Loc.T("telegram.wipeTelegramStarting");
                var client = new TelegramBotClient();
                var (deleted, localCleared) = await TelegramChatCleaner.WipeTelegramAndLocalAsync(
                    token,
                    chatId,
                    _openProjectPath,
                    client,
                    CancellationToken.None).ConfigureAwait(true);

                try
                {
                    var doneId = await client.SendMessageAsync(
                        token,
                        chatId,
                        TelegramChatCleaner.WipeSummary(deleted, localCleared),
                        CancellationToken.None,
                        TelegramDeployCoordinator.BuildReplyKeyboard()).ConfigureAwait(true);
                    _store.TrackBotMessageId(_openProjectPath, doneId);
                }
                catch
                {
                }

                ChatStatusText.Text = TelegramChatCleaner.WipeSummary(deleted, localCleared);
            }
            finally
            {
                WipeTelegramButton.IsEnabled = true;
                ClearChatButton.IsEnabled = true;
            }
        }

        private void UpdateThreadPreview(string projectPath)
        {
            var summary = _store.ListThreads(new[] { projectPath })
                .FirstOrDefault(t => string.Equals(t.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
            var vm = _threads.FirstOrDefault(t =>
                string.Equals(t.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
            if (summary != null && vm != null)
            {
                vm.Apply(summary);
                return;
            }

            RefreshThreads(_openProjectPath);
        }

        private void ScrollMessagesToEnd()
        {
            if (_messages.Count == 0)
            {
                return;
            }

            MessagesList.ScrollIntoView(_messages[^1]);
        }

        private void AttachPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.gif;*.webp|All files|*.*",
                Title = Loc.T("telegram.attach")
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _pendingPhotoPath = dialog.FileName;
            PendingPhotoBar.Visibility = Visibility.Visible;
            PendingPhotoText.Text = Path.GetFileName(dialog.FileName);
        }

        private void ClearPendingPhoto_Click(object sender, RoutedEventArgs e)
        {
            _pendingPhotoPath = null;
            PendingPhotoBar.Visibility = Visibility.Collapsed;
        }

        private void ComposerBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }

        private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ComposerBox == null)
            {
                return;
            }

            ComposerBox.FlowDirection = TelegramTextFormat.DetectFlow(ComposerBox.Text);
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SendAsync();
        }

        private async System.Threading.Tasks.Task SendAsync()
        {
            var text = ComposerBox?.Text?.Trim() ?? string.Empty;
            var photo = _pendingPhotoPath;
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(photo))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_openProjectPath))
            {
                ModernMessageBox.Show(Loc.T("telegram.pickThread"), Loc.T("telegram.title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await TelegramPoller.Instance.SendOutgoingAsync(_openProjectPath, text, photo, System.Threading.CancellationToken.None);
                if (ComposerBox != null)
                {
                    ComposerBox.Text = string.Empty;
                }
                ClearPendingPhoto_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    Loc.T("telegram.sendFailed", ex.Message),
                    Loc.T("common.error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Photo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string path && File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }
    }

    public sealed class TelegramThreadItemVm : INotifyPropertyChanged
    {
        public TelegramThreadItemVm(TelegramThreadSummary summary)
        {
            ProjectPath = summary.ProjectPath;
            Apply(summary);
        }

        public string ProjectPath { get; }

        public string DisplayName { get; private set; } = "";
        public string LastPreview { get; private set; } = "";
        public string TimeLabel { get; private set; } = "";
        public int UnreadCount { get; private set; }
        public Visibility UnreadVisibility { get; private set; } = Visibility.Collapsed;
        public string WorkspaceBadge { get; private set; } = "";
        public Visibility WorkspaceBadgeVisibility { get; private set; } = Visibility.Collapsed;
        public string Initial { get; private set; } = "?";
        public System.Windows.Media.Brush AvatarBrush { get; private set; } = System.Windows.Media.Brushes.Gray;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(TelegramThreadSummary summary)
        {
            DisplayName = summary.DisplayName;
            LastPreview = summary.LastPreview;
            TimeLabel = FormatTime(summary.LastMessageUtc);
            UnreadCount = summary.UnreadCount;
            UnreadVisibility = summary.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            Initial = ProjectAvatarHelper.GetInitial(summary.DisplayName);
            AvatarBrush = ProjectAvatarHelper.GetColor(summary.DisplayName);

            var workspace = CursorWorkspaceRoots.GetRoots(summary.ProjectPath);
            WorkspaceBadge = CursorWorkspaceRoots.FormatBadge(workspace);
            WorkspaceBadgeVisibility = workspace.HasMultiRoot ? Visibility.Visible : Visibility.Collapsed;

            OnPropertyChanged(string.Empty);
        }

        private static string FormatTime(DateTime? utc)
        {
            if (!utc.HasValue)
            {
                return string.Empty;
            }

            var local = utc.Value.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm")
                : local.ToString("MMM d");
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class TelegramMessageItemVm
    {
        public TelegramMessageItemVm(TelegramChatMessage message)
        {
            Text = message.Text ?? string.Empty;
            PhotoPath = message.PhotoPath ?? string.Empty;
            IsOutgoing = message.Direction == TelegramMessageDirection.Outgoing;
            IsSystem = message.Direction == TelegramMessageDirection.System;
            TimeLabel = message.Utc.ToLocalTime().ToString("HH:mm");
            PhotoVisibility = File.Exists(PhotoPath) ? Visibility.Visible : Visibility.Collapsed;
            TextVisibility = string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
            PhotoImage = LoadImage(PhotoPath);
            TextFlow = TelegramTextFormat.DetectFlow(Text);
            TextAlign = TelegramTextFormat.DetectAlignment(Text);
        }

        public string Text { get; }
        public string PhotoPath { get; }
        public bool IsOutgoing { get; }
        public bool IsSystem { get; }
        public string TimeLabel { get; }
        public Visibility PhotoVisibility { get; }
        public Visibility TextVisibility { get; }
        public ImageSource? PhotoImage { get; }
        public System.Windows.FlowDirection TextFlow { get; }
        public System.Windows.TextAlignment TextAlign { get; }

        private static ImageSource? LoadImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
