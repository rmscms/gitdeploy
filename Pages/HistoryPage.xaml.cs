using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitDeployPro.Controls;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class HistoryPage : Page
    {
        private const int PageSize = 50;
        private const int SuggestionLimit = 20;
        private const int MaxIndexedPaths = 4000;
        private const int MaxHitsPerPath = 60;
        private const int MaxIndexedHits = 30000;

        private static readonly SolidColorBrush AddedBrush = ResolveStatusBrush("Status.Success", 46, 125, 50);
        private static readonly SolidColorBrush ModifiedBrush = ResolveStatusBrush("Status.Warning", 255, 143, 0);
        private static readonly SolidColorBrush DeletedBrush = ResolveStatusBrush("Status.Error", 198, 40, 40);

        private readonly HistoryService _historyService;
        private readonly GitService _gitService;
        private readonly ObservableCollection<DeploymentRecord> _historyItems;
        private readonly ObservableCollection<string> _suggestions;
        private readonly ObservableCollection<HistoryFileHitItem> _fileHits;
        private readonly Dictionary<string, List<HistoryFileHitItem>> _fileIndex;
        private readonly Queue<string> _fileIndexOrder;
        private readonly List<DeploymentRecord> _localHistoryBuffer;
        private readonly DispatcherTimer _searchDebounceTimer;
        private readonly HashSet<string> _loadedCommitHashes;

        private bool _isLoading;
        private bool _isGitSource;
        private bool _hasMore;
        private bool _isApplyingSuggestion;
        private int _localHistoryCursor;
        private int _totalCommitCount;
        private string? _oldestLoadedCommitHash;
        private string _currentBranch = string.Empty;
        private int _indexedHitCount;

        public HistoryPage()
        {
            InitializeComponent();
            _historyService = new HistoryService();
            _gitService = new GitService();
            _historyItems = new ObservableCollection<DeploymentRecord>();
            _suggestions = new ObservableCollection<string>();
            _fileHits = new ObservableCollection<HistoryFileHitItem>();
            _fileIndex = new Dictionary<string, List<HistoryFileHitItem>>(StringComparer.OrdinalIgnoreCase);
            _fileIndexOrder = new Queue<string>();
            _localHistoryBuffer = new List<DeploymentRecord>();
            _loadedCommitHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            HistoryListBox.ItemsSource = _historyItems;
            SuggestionsListBox.ItemsSource = _suggestions;
            FileHitsListBox.ItemsSource = _fileHits;

            Loaded += HistoryPage_Loaded;
            Unloaded += HistoryPage_Unloaded;
        }

        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= HistoryPage_Loaded;
            await InitializeHistoryAsync();
        }

        private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            Unloaded -= HistoryPage_Unloaded;
        }

        private async Task InitializeHistoryAsync()
        {
            using var scope = PerformanceSampler.Instance.BeginScope("history", "initialize");
            ResetState();
            SelectedFileText.Text = "Select a suggested file to see commits.";
            HistoryEmptyText.Visibility = Visibility.Collapsed;

            if (_gitService.IsGitRepository())
            {
                _isGitSource = true;

                try
                {
                    _currentBranch = await _gitService.GetCurrentBranchAsync();
                }
                catch
                {
                    _currentBranch = string.Empty;
                }

                try
                {
                    _totalCommitCount = await _gitService.GetTotalCommitsAsync();
                }
                catch
                {
                    _totalCommitCount = 0;
                }

                _hasMore = true;
                await LoadNextGitPageAsync();
            }
            else
            {
                _isGitSource = false;
                var localHistory = _historyService.GetHistory()
                    .OrderByDescending(record => record.Date)
                    .ToList();

                _localHistoryBuffer.AddRange(localHistory);
                _totalCommitCount = _localHistoryBuffer.Count;
                _hasMore = _localHistoryBuffer.Count > 0;

                await LoadNextLocalPageAsync();
            }

            UpdateSummaryText();
            UpdateLoadMoreState();
            HistoryEmptyText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResetState()
        {
            _searchDebounceTimer.Stop();
            _historyItems.Clear();
            _suggestions.Clear();
            _fileHits.Clear();
            _fileIndex.Clear();
            _fileIndexOrder.Clear();
            _localHistoryBuffer.Clear();
            _loadedCommitHashes.Clear();

            _isLoading = false;
            _hasMore = false;
            _isApplyingSuggestion = false;
            _localHistoryCursor = 0;
            _totalCommitCount = 0;
            _oldestLoadedCommitHash = null;
            _indexedHitCount = 0;
        }

        private async Task LoadNextGitPageAsync()
        {
            using var scope = PerformanceSampler.Instance.BeginScope("history", "load-next-git-page");
            if (_isLoading || !_hasMore)
            {
                return;
            }

            _isLoading = true;
            UpdateLoadMoreState();

            try
            {
                var page = await _gitService.GetCommitHistoryWithFilesPageAsync(PageSize, _oldestLoadedCommitHash);
                if (page.Count == 0)
                {
                    _hasMore = false;
                    return;
                }

                foreach (var entry in page)
                {
                    if (string.IsNullOrWhiteSpace(entry.Commit.FullHash))
                    {
                        continue;
                    }

                    if (_loadedCommitHashes.Contains(entry.Commit.FullHash))
                    {
                        continue;
                    }

                    var record = CreateRecordFromCommit(entry);
                    _historyItems.Add(record);
                    _loadedCommitHashes.Add(entry.Commit.FullHash);
                    IndexRecordFiles(record, entry.ChangedFiles, entry.Commit.ShortHash, entry.Commit.Message);
                }

                _oldestLoadedCommitHash = page.Last().Commit.FullHash;
                _hasMore = page.Count >= PageSize;
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                _hasMore = false;
                ModernMessageBox.Show(
                    $"Unable to load commit history: {ex.Message}",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    context: this);
            }
            finally
            {
                _isLoading = false;
                UpdateSummaryText();
                UpdateLoadMoreState();
                HistoryEmptyText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (!string.IsNullOrWhiteSpace(FileSearchTextBox.Text))
                {
                    ApplySearchSuggestions();
                }
            }
        }

        private Task LoadNextLocalPageAsync()
        {
            using var scope = PerformanceSampler.Instance.BeginScope("history", "load-next-local-page");
            if (_isLoading || !_hasMore)
            {
                return Task.CompletedTask;
            }

            _isLoading = true;
            UpdateLoadMoreState();

            try
            {
                var nextItems = _localHistoryBuffer
                    .Skip(_localHistoryCursor)
                    .Take(PageSize)
                    .ToList();

                foreach (var record in nextItems)
                {
                    _historyItems.Add(record);
                    if (!string.IsNullOrWhiteSpace(record.CommitHash))
                    {
                        _loadedCommitHashes.Add(record.CommitHash);
                    }

                    IndexRecordFiles(
                        record,
                        fileChanges: null,
                        commitShortHash: ResolveShortHash(record.CommitHash, record.Title),
                        commitMessage: ExtractCommitMessage(record.Title));
                }

                _localHistoryCursor += nextItems.Count;
                _hasMore = _localHistoryCursor < _localHistoryBuffer.Count;
            }
            finally
            {
                _isLoading = false;
                UpdateSummaryText();
                UpdateLoadMoreState();
                HistoryEmptyText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (!string.IsNullOrWhiteSpace(FileSearchTextBox.Text))
                {
                    ApplySearchSuggestions();
                }
            }

            return Task.CompletedTask;
        }

        private DeploymentRecord CreateRecordFromCommit(CommitHistoryEntry entry)
        {
            var files = entry.ChangedFiles
                .Select(change => change.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DeploymentRecord
            {
                Id = _historyItems.Count + 1,
                Title = $"{entry.Commit.ShortHash} - {entry.Commit.Message}",
                Date = entry.Commit.Date,
                FilesCount = files.Count,
                Branch = _currentBranch,
                Status = "Success",
                Files = files,
                CommitHash = entry.Commit.FullHash
            };
        }

        private void IndexRecordFiles(
            DeploymentRecord record,
            List<CommitFileChangeInfo>? fileChanges,
            string? commitShortHash,
            string? commitMessage)
        {
            var normalizedCommitShortHash = ResolveShortHash(record.CommitHash, record.Title, commitShortHash);
            var normalizedCommitMessage = string.IsNullOrWhiteSpace(commitMessage)
                ? ExtractCommitMessage(record.Title)
                : commitMessage.Trim();

            IEnumerable<CommitFileChangeInfo> sourceChanges =
                fileChanges != null && fileChanges.Count > 0
                    ? fileChanges
                    : (record.Files ?? new List<string>()).Select(file => new CommitFileChangeInfo
                    {
                        Path = file,
                        Type = ChangeType.Modified,
                        StatusCode = "M"
                    });

            foreach (var change in sourceChanges)
            {
                var normalizedPath = NormalizePath(change.Path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                if (!_fileIndex.TryGetValue(normalizedPath, out var hits))
                {
                    if (_fileIndex.Count >= MaxIndexedPaths)
                    {
                        RemoveOldestIndexedPath();
                    }

                    hits = new List<HistoryFileHitItem>();
                    _fileIndex[normalizedPath] = hits;
                    _fileIndexOrder.Enqueue(normalizedPath);
                }

                if (hits.Any(hit =>
                        string.Equals(hit.CommitHash, record.CommitHash, StringComparison.OrdinalIgnoreCase) &&
                        hit.Date == record.Date))
                {
                    continue;
                }

                hits.Add(new HistoryFileHitItem
                {
                    FilePath = normalizedPath,
                    CommitHash = record.CommitHash ?? string.Empty,
                    CommitSummary = string.IsNullOrWhiteSpace(normalizedCommitMessage) ? record.Title : normalizedCommitMessage,
                    CommitShortHash = normalizedCommitShortHash,
                    Date = record.Date,
                    ChangeType = change.Type,
                    ChangeTypeLabel = ToChangeTypeLabel(change.Type),
                    ChangeTypeBrush = ToChangeTypeBrush(change.Type)
                });
                _indexedHitCount++;

                if (hits.Count > MaxHitsPerPath)
                {
                    hits.RemoveAt(hits.Count - 1);
                    _indexedHitCount--;
                }
            }

            TrimIndexIfNeeded();
        }

        private void RemoveOldestIndexedPath()
        {
            while (_fileIndexOrder.Count > 0)
            {
                var key = _fileIndexOrder.Dequeue();
                if (_fileIndex.Remove(key, out var removedHits))
                {
                    _indexedHitCount = Math.Max(0, _indexedHitCount - removedHits.Count);
                    return;
                }
            }
        }

        private void TrimIndexIfNeeded()
        {
            while (_indexedHitCount > MaxIndexedHits && _fileIndexOrder.Count > 0)
            {
                RemoveOldestIndexedPath();
            }
        }

        private void FileSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSuggestion)
            {
                return;
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            ApplySearchSuggestions();
        }

        private void ApplySearchSuggestions()
        {
            var query = NormalizePath(FileSearchTextBox.Text);
            if (string.IsNullOrWhiteSpace(query))
            {
                _suggestions.Clear();
                _fileHits.Clear();
                SuggestionsBorder.Visibility = Visibility.Collapsed;
                SelectedFileText.Text = "Select a suggested file to see commits.";
                return;
            }

            var matches = _fileIndex.Keys
                .Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestionLimit)
                .ToList();

            ReplaceCollection(_suggestions, matches);
            SuggestionsBorder.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_fileIndex.TryGetValue(query, out _))
            {
                ShowFileHits(query);
            }
            else if (matches.Count == 0)
            {
                _fileHits.Clear();
                SelectedFileText.Text = "No matching file found in loaded commits.";
            }
        }

        private void SuggestionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SuggestionsListBox.SelectedItem is string selectedPath)
            {
                SelectSuggestedPath(selectedPath);
            }
        }

        private void FileSearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
            {
                return;
            }

            if (SuggestionsListBox.SelectedItem is string selectedPath)
            {
                SelectSuggestedPath(selectedPath);
            }
            else if (_suggestions.Count > 0)
            {
                SelectSuggestedPath(_suggestions[0]);
            }

            e.Handled = true;
        }

        private void SelectSuggestedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _isApplyingSuggestion = true;
            FileSearchTextBox.Text = path;
            FileSearchTextBox.CaretIndex = FileSearchTextBox.Text.Length;
            _isApplyingSuggestion = false;

            SuggestionsBorder.Visibility = Visibility.Collapsed;
            ShowFileHits(path);
        }

        private void ShowFileHits(string path)
        {
            if (!_fileIndex.TryGetValue(path, out var hits))
            {
                _fileHits.Clear();
                SelectedFileText.Text = "No commit hit available for this file.";
                return;
            }

            var orderedHits = hits
                .OrderByDescending(hit => hit.Date)
                .ToList();

            ReplaceCollection(_fileHits, orderedHits);
            SelectedFileText.Text = $"{path} ({orderedHits.Count} hit{(orderedHits.Count == 1 ? string.Empty : "s")})";
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            FileSearchTextBox.Clear();
            _suggestions.Clear();
            _fileHits.Clear();
            SuggestionsBorder.Visibility = Visibility.Collapsed;
            SelectedFileText.Text = "Select a suggested file to see commits.";
            SuggestionsListBox.SelectedItem = null;
        }

        private async void OpenFileContent_Click(object sender, RoutedEventArgs e)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("history", "open-file-content");
            if (sender is not System.Windows.Controls.Button button || button.Tag is not HistoryFileHitItem hit)
            {
                return;
            }

            if (!hit.CanOpen)
            {
                ModernMessageBox.Show(
                    "This entry does not include a commit hash, so file snapshot cannot be opened.",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    context: this);
                return;
            }

            try
            {
                var content = await _gitService.GetCommitFileContentAsync(hit.CommitHash, hit.FilePath);
                var viewer = new CodeViewerWindow(hit.FilePath, content, string.Empty, readOnlyOnly: true);
                WindowOwnerService.ShowDialogOwned(viewer, this);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                ModernMessageBox.Show(
                    $"Unable to open file content: {ex.Message}",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    context: this);
            }
        }

        private async void ViewFileDiff_Click(object sender, RoutedEventArgs e)
        {
            using var scope = PerformanceSampler.Instance.BeginScope("history", "open-file-diff");
            if (sender is not System.Windows.Controls.Button button || button.Tag is not HistoryFileHitItem hit)
            {
                return;
            }

            if (!hit.CanOpen)
            {
                ModernMessageBox.Show(
                    "This entry does not include a commit hash, so diff cannot be loaded.",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    context: this);
                return;
            }

            try
            {
                var diff = await _gitService.GetCommitFileDiffAsync(hit.CommitHash, hit.FilePath);
                var diffWindow = new ReadOnlyDiffWindow(hit.FilePath, hit.ChangeTypeLabel, diff);
                WindowOwnerService.ShowDialogOwned(diffWindow, this);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                ModernMessageBox.Show(
                    $"Unable to open file diff: {ex.Message}",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    context: this);
            }
        }

        private async void LoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (_isGitSource)
            {
                await LoadNextGitPageAsync();
            }
            else
            {
                await LoadNextLocalPageAsync();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await InitializeHistoryAsync();
            ModernMessageBox.Show(
                "History refreshed.",
                "History",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                context: this);
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button || button.Tag is not DeploymentRecord record)
            {
                return;
            }

            var detailsWindow = new HistoryDetailsWindow(record);
            WindowOwnerService.ShowDialogOwned(detailsWindow, this);
        }

        private async void Rollback_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button || button.Tag is not DeploymentRecord record)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(record.CommitHash))
            {
                ModernMessageBox.Show(
                    "Cannot rollback this item because commit hash is not available.",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    context: this);
                return;
            }

            var decision = ModernMessageBox.ShowWithResult(
                $"Are you sure you want to rollback this commit?\n\nCommit: {record.Title}\n\nThis creates a new revert commit.",
                "Confirm rollback",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                primaryText: "Yes, rollback",
                secondaryText: "No",
                context: this);

            if (decision != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _gitService.RevertCommitAsync(record.CommitHash);
                ModernMessageBox.Show(
                    "Rollback successful. A new revert commit has been created.",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    context: this);
                await InitializeHistoryAsync();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Rollback failed: {ex.Message}",
                    "History",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    context: this);
            }
        }

        private void UpdateLoadMoreState()
        {
            if (LoadMoreButton == null)
            {
                return;
            }

            if (_isLoading)
            {
                LoadMoreButton.IsEnabled = false;
                LoadMoreButton.Content = "Loading...";
                return;
            }

            LoadMoreButton.IsEnabled = _hasMore;
            LoadMoreButton.Content = _hasMore ? "Load more" : "No more commits";
        }

        private void UpdateSummaryText()
        {
            if (LoadedSummaryText == null)
            {
                return;
            }

            int loaded = _historyItems.Count;
            if (_totalCommitCount > 0)
            {
                LoadedSummaryText.Text = $"Loaded: {loaded} / {_totalCommitCount}";
                return;
            }

            LoadedSummaryText.Text = $"Loaded: {loaded}";
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private static string ExtractCommitMessage(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            int separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex < 0 || separatorIndex + 3 >= title.Length)
            {
                return title.Trim();
            }

            return title[(separatorIndex + 3)..].Trim();
        }

        private static string ResolveShortHash(string commitHash, string title, string? preferredShortHash = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredShortHash))
            {
                return preferredShortHash.Trim();
            }

            if (!string.IsNullOrWhiteSpace(commitHash))
            {
                return commitHash.Length > 7 ? commitHash[..7] : commitHash;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            int separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return string.Empty;
            }

            var candidate = title[..separatorIndex].Trim();
            return candidate.Length <= 12 ? candidate : string.Empty;
        }

        private static string NormalizePath(string path)
        {
            return path?.Replace("\\", "/").Trim() ?? string.Empty;
        }

        private static string ToChangeTypeLabel(ChangeType changeType)
        {
            return changeType switch
            {
                ChangeType.Added => "NEW",
                ChangeType.Deleted => "DELETED",
                _ => "MODIFIED"
            };
        }

        private static SolidColorBrush ToChangeTypeBrush(ChangeType changeType)
        {
            return changeType switch
            {
                ChangeType.Added => AddedBrush,
                ChangeType.Deleted => DeletedBrush,
                _ => ModifiedBrush
            };
        }

        private static SolidColorBrush ResolveStatusBrush(string resourceKey, byte red, byte green, byte blue)
        {
            if (System.Windows.Application.Current?.TryFindResource(resourceKey) is SolidColorBrush themedBrush)
            {
                return themedBrush;
            }

            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        public sealed class HistoryFileHitItem
        {
            public string FilePath { get; set; } = string.Empty;
            public string CommitHash { get; set; } = string.Empty;
            public string CommitShortHash { get; set; } = string.Empty;
            public string CommitSummary { get; set; } = string.Empty;
            public DateTime Date { get; set; }
            public ChangeType ChangeType { get; set; } = ChangeType.Modified;
            public string ChangeTypeLabel { get; set; } = "MODIFIED";
            public SolidColorBrush ChangeTypeBrush { get; set; } = ModifiedBrush;

            public bool CanOpen => !string.IsNullOrWhiteSpace(CommitHash);
            public string DateText => Date.ToString("yyyy/MM/dd HH:mm");
            public string CommitShortHashText => string.IsNullOrWhiteSpace(CommitShortHash) ? string.Empty : $"#{CommitShortHash}";
        }
    }
}
