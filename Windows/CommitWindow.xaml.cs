using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Services;
using GitDeployPro.Models;

namespace GitDeployPro.Windows
{
    public partial class CommitWindow : Window
    {
        public bool Confirmed { get; private set; } = false;
        public bool CommitAndDeployRequested { get; private set; } = false;
        public bool SyncWithoutDeployRequested { get; private set; } = false;
        public string SyncWithoutDeployPath { get; private set; } = string.Empty;
        public string CommitMessage 
        { 
            get => CommitMessageTextBox?.Text ?? ""; 
            set 
            {
                if (CommitMessageTextBox != null)
                {
                    CommitMessageTextBox.Text = value;
                }
            }
        }
        private List<FileChange> _changes;
        private List<FileChangeViewModel> _viewModels = new List<FileChangeViewModel>();

        public CommitWindow(List<FileChange> changes)
        {
            InitializeComponent();
            _changes = changes;
            LoadChanges();
        }

        private void LoadChanges()
        {
            TotalChangesText.Text = $"{_changes.Count} Files";
            
            _viewModels = _changes.Select(f => new FileChangeViewModel(f)).ToList();
            FilesListBox.ItemsSource = _viewModels;
            FilesListBox.SelectedIndex = _viewModels.Count > 0 ? 0 : -1;
            UpdateDiffPreview();
        }

        private void CommitAndDeploy_Click(object sender, RoutedEventArgs e)
        {
            string message = CommitMessageTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"deploy update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}";
            }

            CommitMessage = message;
            Confirmed = true;
            CommitAndDeployRequested = true;
            SyncWithoutDeployRequested = false;
            SyncWithoutDeployPath = string.Empty;
            this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }

        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string relativePath)
            {
                try
                {
                    string fullPath = Path.GetFullPath(relativePath);
                    // If file doesn't exist (e.g. deleted), open folder
                    if (!File.Exists(fullPath))
                    {
                        fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
                    }
                    
                    Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show($"Could not open explorer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string relativePath)
            {
                var result = ModernMessageBox.Show($"Are you sure you want to delete '{relativePath}' permanently?", 
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result)
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(relativePath);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            
                            // Remove from list
                            var itemToRemove = _changes.FirstOrDefault(c => c.Name == relativePath);
                            if (itemToRemove != null) _changes.Remove(itemToRemove);
                            
                            LoadChanges(); // Refresh UI
                        }
                        else
                        {
                            ModernMessageBox.Show("File not found (maybe already deleted).", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModernMessageBox.Show($"Could not delete file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDiffPreview();
        }

        private void UpdateDiffPreview()
        {
            if (CommitDiffViewer == null) return;

            if (FilesListBox?.SelectedItem is FileChangeViewModel vm)
            {
                CommitDiffViewer.Title = vm.Name;
                CommitDiffViewer.Status = vm.StatusText;
                CommitDiffViewer.FilePath = vm.Name;
                CommitDiffViewer.DiffText = vm.DiffText;
            }
            else
            {
                CommitDiffViewer.Title = "Diff preview";
                CommitDiffViewer.Status = string.Empty;
                CommitDiffViewer.FilePath = string.Empty;
                CommitDiffViewer.DiffText = string.Empty;
            }
        }

        private void FilesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FilesListBox?.SelectedItem is FileChangeViewModel vm)
            {
                OpenCodeViewer(vm);
            }
        }

        private void FilesListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var listItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (listItem != null)
            {
                listItem.IsSelected = true;
                listItem.Focus();
            }
        }

        private async void AddToGitIgnoreMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FilesListBox?.SelectedItem is not FileChangeViewModel vm)
            {
                ModernMessageBox.Show("Select a file or folder first.", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ignoreEntry = NormalizeGitIgnoreEntry(vm.Name);
            if (string.IsNullOrWhiteSpace(ignoreEntry))
            {
                ModernMessageBox.Show("Unable to build .gitignore pattern for this path.", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                $"Add '{ignoreEntry}' to .gitignore?",
                "Git ignore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                "Add",
                "Cancel");

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var projectRoot = ResolveWorkingDirectory();
                var gitIgnorePath = Path.Combine(projectRoot, ".gitignore");
                EnsureGitIgnoreEntry(gitIgnorePath, ignoreEntry, out var addedNow);

                // If path is already tracked, untrack it so gitignore can take effect.
                var gitService = new GitService();
                await gitService.RemovePathFromIndexAsync(ignoreEntry.TrimEnd('/'));

                _changes = await gitService.GetUncommittedChangesAsync(includeDiff: true);
                LoadChanges();

                var message = addedNow
                    ? $"'{ignoreEntry}' added to .gitignore."
                    : $"'{ignoreEntry}' was already in .gitignore.";
                ModernMessageBox.Show(message, "Git ignore", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to update .gitignore: {ex.Message}", "Git ignore", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SyncWithoutDeployMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FilesListBox?.SelectedItem is not FileChangeViewModel vm)
            {
                ModernMessageBox.Show("Select a file first.", "Sync", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                $"Sync branches without FTP deploy?\n\nSelected file: {vm.Name}\n\nOnly this selected file will be committed for sync, then branches are synced and pushed if remote exists.",
                "Sync (No Deploy)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                "Sync",
                "Cancel");

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            string message = CommitMessageTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"sync update {AppTimeService.LocalNow:yyyy-MM-dd HH:mm}";
            }

            CommitMessage = message;
            Confirmed = true;
            CommitAndDeployRequested = false;
            SyncWithoutDeployRequested = true;
            SyncWithoutDeployPath = vm.Name;
            Close();
        }

        private void OpenCodeViewer(FileChangeViewModel vm)
        {
            try
            {
                var root = GitService.WorkingDirectoryPath;
                var normalized = vm.Name.Replace('/', Path.DirectorySeparatorChar);
                var absolute = string.IsNullOrWhiteSpace(root) ? normalized : Path.Combine(root, normalized);
                var content = File.Exists(absolute) ? File.ReadAllText(absolute) : vm.DiffText ?? string.Empty;
                var viewer = new CodeViewerWindow(vm.Name, content, absolute)
                {
                    Owner = this
                };
                viewer.ShowDialog();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to open viewer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FileChangeViewModel vm)
            {
                OpenCodeViewer(vm);
                e.Handled = true;
            }
        }

        private static string ResolveWorkingDirectory()
        {
            return string.IsNullOrWhiteSpace(GitService.WorkingDirectoryPath)
                ? Directory.GetCurrentDirectory()
                : GitService.WorkingDirectoryPath;
        }

        private static string NormalizeGitIgnoreEntry(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            var normalized = relativePath.Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            normalized = normalized.TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
            if (!isDirectory)
            {
                var fullPath = Path.Combine(ResolveWorkingDirectory(), normalized.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(fullPath) && !File.Exists(fullPath))
                {
                    isDirectory = true;
                }
            }

            return isDirectory
                ? normalized.TrimEnd('/') + "/"
                : normalized;
        }

        private static void EnsureGitIgnoreEntry(string gitIgnorePath, string ignoreEntry, out bool addedNow)
        {
            var lines = File.Exists(gitIgnorePath)
                ? File.ReadAllLines(gitIgnorePath).ToList()
                : new List<string>();

            var exists = lines.Any(line => string.Equals(line.Trim(), ignoreEntry, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                addedNow = false;
                return;
            }

            lines.Add(ignoreEntry);
            File.WriteAllLines(gitIgnorePath, lines);
            addedNow = true;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T matched)
                {
                    return matched;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }

    public class FileChangeViewModel
    {
        public string Name { get; set; }
        public ChangeType Type { get; set; }
        public string DiffText { get; }
        
        public string StatusText 
        {
            get
            {
                switch (Type)
                {
                    case ChangeType.Added: return "NEW";
                    case ChangeType.Modified: return "MODIFIED";
                    case ChangeType.Deleted: return "DELETED";
                    default: return "";
                }
            }
        }

        public SolidColorBrush StatusColor
        {
            get
            {
                switch (Type)
                {
                    case ChangeType.Added: return ResolveThemeBrush("Status.Success", System.Windows.Media.Color.FromRgb(76, 210, 126));
                    case ChangeType.Modified: return ResolveThemeBrush("Status.Warning", System.Windows.Media.Color.FromRgb(255, 191, 71));
                    case ChangeType.Deleted: return ResolveThemeBrush("Status.Error", System.Windows.Media.Color.FromRgb(255, 108, 122));
                    default: return ResolveThemeBrush("Text.Muted", System.Windows.Media.Colors.Gray);
                }
            }
        }

        private static SolidColorBrush ResolveThemeBrush(string resourceKey, System.Windows.Media.Color fallbackColor)
        {
            if (System.Windows.Application.Current?.Resources[resourceKey] is SolidColorBrush themedBrush)
            {
                return themedBrush;
            }

            return new SolidColorBrush(fallbackColor);
        }

        public FileChangeViewModel(FileChange change)
        {
            Name = change.Name;
            Type = change.Type;
            DiffText = change.DiffPatch ?? string.Empty;
        }
    }
}
