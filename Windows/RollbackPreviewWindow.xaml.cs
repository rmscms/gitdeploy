using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public enum RollbackScope
    {
        None,
        WholeCommit,
        SingleFile
    }

    public partial class RollbackPreviewWindow : Window
    {
        private readonly GitService _gitService;
        private readonly List<CommitHistoryEntry> _commits = new();
        private int _index;
        private bool _loading;
        private bool _loadingMore;

        public bool Confirmed => Scope != RollbackScope.None;
        public RollbackScope Scope { get; private set; } = RollbackScope.None;
        public bool RedeployRequested => RedeployCheckBox?.IsChecked == true;
        public CommitHistoryEntry? Entry => _index >= 0 && _index < _commits.Count ? _commits[_index] : null;
        public CommitFileChangeInfo? SelectedFile { get; private set; }

        public RollbackPreviewWindow(GitService gitService, bool canRedeploy)
        {
            InitializeComponent();
            _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));

            RedeployCheckBox.IsEnabled = canRedeploy;
            RedeployCheckBox.IsChecked = canRedeploy;
            if (!canRedeploy)
            {
                RedeployCheckBox.Content = "FTP redeploy unavailable (no connection assigned)";
                RedeployCheckBox.Opacity = 0.7;
            }

            Loaded += async (_, _) => await LoadInitialAsync();
        }

        private async Task LoadInitialAsync()
        {
            _loading = true;
            try
            {
                CommitMessageText.Text = "Loading commits…";
                var page = await _gitService.GetCommitHistoryWithFilesPageAsync(20);
                _commits.Clear();
                _commits.AddRange(page.Where(e => e?.Commit != null && !string.IsNullOrWhiteSpace(e.Commit.FullHash)));
                _index = 0;
                BindCurrentCommit();
                if (FilesListBox.Items.Count > 0)
                {
                    FilesListBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                CommitMessageText.Text = "Failed to load history";
                CommitMetaText.Text = ex.Message;
                ConfirmButton.IsEnabled = false;
                OlderCommitButton.IsEnabled = false;
                NewerCommitButton.IsEnabled = false;
            }
            finally
            {
                _loading = false;
                UpdateNavButtons();
            }
        }

        private void BindCurrentCommit()
        {
            var entry = Entry;
            if (entry?.Commit == null)
            {
                CommitHashText.Text = "—";
                CommitMessageText.Text = "No commits found";
                CommitMetaText.Text = string.Empty;
                FileCountText.Text = "0";
                FilesListBox.ItemsSource = null;
                FileDiffViewer.DiffText = string.Empty;
                FileDiffViewer.Title = "Diff preview";
                FileDiffViewer.FilePath = string.Empty;
                FileDiffViewer.Status = string.Empty;
                ConfirmButton.IsEnabled = false;
                RollbackFileButton.IsEnabled = false;
                CommitPositionText.Text = string.Empty;
                UpdateNavButtons();
                return;
            }

            var commit = entry.Commit;
            CommitHashText.Text = string.IsNullOrWhiteSpace(commit.ShortHash) ? "—" : commit.ShortHash;
            CommitMessageText.Text = string.IsNullOrWhiteSpace(commit.Message) ? "(no message)" : commit.Message;
            CommitMetaText.Text = $"{commit.Author} · {commit.Date:yyyy-MM-dd HH:mm}";
            CommitPositionText.Text = $"{_index + 1} / {_commits.Count}";

            var files = (entry.ChangedFiles ?? new List<CommitFileChangeInfo>())
                .Select(f => new RollbackFileRow(f))
                .ToList();
            FilesListBox.ItemsSource = files;
            FileCountText.Text = $"{files.Count} file(s)";
            ConfirmButton.IsEnabled = true;
            SelectedFile = null;
            RollbackFileButton.IsEnabled = false;
            FileDiffViewer.DiffText = string.Empty;
            FileDiffViewer.Title = "Diff preview";
            FileDiffViewer.FilePath = string.Empty;
            FileDiffViewer.Status = "Select a file";
            UpdateNavButtons();
        }

        private void UpdateNavButtons()
        {
            NewerCommitButton.IsEnabled = !_loading && _index > 0;
            OlderCommitButton.IsEnabled = !_loading && (_index < _commits.Count - 1 || !_loadingMore);
        }

        private async void OlderCommitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _loadingMore)
            {
                return;
            }

            if (_index < _commits.Count - 1)
            {
                _index++;
                BindCurrentCommit();
                if (FilesListBox.Items.Count > 0)
                {
                    FilesListBox.SelectedIndex = 0;
                }

                return;
            }

            var last = _commits.LastOrDefault();
            if (last?.Commit == null || string.IsNullOrWhiteSpace(last.Commit.FullHash))
            {
                return;
            }

            _loadingMore = true;
            OlderCommitButton.IsEnabled = false;
            OlderCommitButton.Content = "Loading…";
            try
            {
                var more = await _gitService.GetCommitHistoryWithFilesPageAsync(20, last.Commit.FullHash);
                var added = more
                    .Where(e => e?.Commit != null && !string.IsNullOrWhiteSpace(e.Commit.FullHash))
                    .Where(e => _commits.All(c =>
                        !string.Equals(c.Commit.FullHash, e.Commit.FullHash, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (added.Count == 0)
                {
                    ModernMessageBox.Show("No older commits found.", "Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _commits.AddRange(added);
                _index++;
                BindCurrentCommit();
                if (FilesListBox.Items.Count > 0)
                {
                    FilesListBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Could not load older commits:\n{ex.Message}", "Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _loadingMore = false;
                OlderCommitButton.Content = "Older →";
                UpdateNavButtons();
            }
        }

        private void NewerCommitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _index <= 0)
            {
                return;
            }

            _index--;
            BindCurrentCommit();
            if (FilesListBox.Items.Count > 0)
            {
                FilesListBox.SelectedIndex = 0;
            }
        }

        private async void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesListBox.SelectedItem is not RollbackFileRow row || Entry?.Commit == null)
            {
                SelectedFile = null;
                RollbackFileButton.IsEnabled = false;
                return;
            }

            SelectedFile = row.File;
            RollbackFileButton.IsEnabled = true;
            RollbackFileButton.Content = $"↩ Rollback file";

            FileDiffViewer.Title = row.DisplayPath;
            FileDiffViewer.FilePath = row.DisplayPath;
            FileDiffViewer.Status = row.StatusText;
            FileDiffViewer.DiffText = "Loading diff…";

            var hash = Entry.Commit.FullHash;
            var path = row.DiffPath;
            try
            {
                var diff = await _gitService.GetCommitFileDiffAsync(hash, path);
                if (FilesListBox.SelectedItem is RollbackFileRow still && still.DiffPath == path)
                {
                    FileDiffViewer.DiffText = string.IsNullOrWhiteSpace(diff)
                        ? "(No diff content for this file.)"
                        : diff;
                }
            }
            catch (Exception ex)
            {
                if (FilesListBox.SelectedItem is RollbackFileRow still && still.DiffPath == path)
                {
                    FileDiffViewer.DiffText = $"Failed to load diff:\n{ex.Message}";
                }
            }
        }

        private void RollbackFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (Entry?.Commit == null || SelectedFile == null)
            {
                return;
            }

            var path = string.IsNullOrWhiteSpace(SelectedFile.Path) ? SelectedFile.OldPath : SelectedFile.Path;
            var confirm = ModernMessageBox.Show(
                $"Rollback only this file from commit {Entry.Commit.ShortHash}?\n\n{path}\n\nA new commit will be created (history is not rewritten).",
                "Rollback file",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (!confirm)
            {
                return;
            }

            Scope = RollbackScope.SingleFile;
            Close();
        }

        private void RollbackCommitButton_Click(object sender, RoutedEventArgs e)
        {
            if (Entry?.Commit == null)
            {
                return;
            }

            var confirm = ModernMessageBox.Show(
                $"Rollback the whole commit {Entry.Commit.ShortHash}?\n\n{Entry.Commit.Message}\n\nThis runs git revert (safe) and then push.",
                "Rollback commit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (!confirm)
            {
                return;
            }

            Scope = RollbackScope.WholeCommit;
            SelectedFile = null;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Scope = RollbackScope.None;
            SelectedFile = null;
            Close();
        }

        private sealed class RollbackFileRow
        {
            public CommitFileChangeInfo File { get; }
            public string DisplayPath { get; }
            public string DiffPath { get; }
            public string Icon { get; }
            public string StatusText { get; }
            public System.Windows.Media.Brush StatusBrush { get; }

            public RollbackFileRow(CommitFileChangeInfo file)
            {
                File = file;
                DisplayPath = string.IsNullOrWhiteSpace(file.OldPath)
                    ? file.Path
                    : $"{file.OldPath} → {file.Path}";
                DiffPath = !string.IsNullOrWhiteSpace(file.Path) ? file.Path : (file.OldPath ?? string.Empty);

                switch (file.Type)
                {
                    case ChangeType.Added:
                        Icon = "➕";
                        StatusText = "was added";
                        StatusBrush = BrushOr("Status.SuccessSurface", "#20382C");
                        break;
                    case ChangeType.Deleted:
                        Icon = "🗑";
                        StatusText = "was deleted";
                        StatusBrush = BrushOr("Status.ErrorSurface", "#3A2428");
                        break;
                    default:
                        Icon = "✎";
                        StatusText = "was changed";
                        StatusBrush = BrushOr("Status.WarningSurface", "#3A301F");
                        break;
                }
            }

            private static System.Windows.Media.Brush BrushOr(string key, string hex)
            {
                if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.Brush brush)
                {
                    return brush;
                }

                try
                {
                    return new SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
                }
                catch
                {
                    return System.Windows.Media.Brushes.DimGray;
                }
            }
        }
    }
}
