using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Services;
using GitDeployPro.Models;

namespace GitDeployPro.Windows
{
    public partial class CommitWindow : Window
    {
        public bool Confirmed { get; private set; } = false;
        public string CommitMessage { get; private set; } = "";
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

        private void Commit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommitMessageTextBox.Text))
            {
                ModernMessageBox.Show("Please enter a commit message.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CommitMessage = CommitMessageTextBox.Text;
            Confirmed = true;
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
                    case ChangeType.Added: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50));
                    case ChangeType.Modified: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 143, 0));
                    case ChangeType.Deleted: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));
                    default: return new SolidColorBrush(System.Windows.Media.Colors.Gray);
                }
            }
        }

        public FileChangeViewModel(FileChange change)
        {
            Name = change.Name;
            Type = change.Type;
            DiffText = change.DiffPatch ?? string.Empty;
        }
    }
}
