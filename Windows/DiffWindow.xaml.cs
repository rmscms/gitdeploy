using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls; // For Button, CheckBox, etc.
using System.Windows.Media;
using GitDeployPro.Models; // For DeployFileViewModel
using GitDeployPro.Services;
using GitDeployPro.Controls; // For ModernMessageBox

namespace GitDeployPro.Windows
{
    public partial class DiffWindow : Window
    {
        public bool Confirmed { get; private set; } = false;
        public List<FileChange> SelectedFiles { get; private set; } = new List<FileChange>();
        
        private List<FileChange> _allChanges;
        private List<DeployFileViewModel> _viewModels = new List<DeployFileViewModel>();

        public DiffWindow(List<FileChange> changes)
        {
            InitializeComponent();
            _allChanges = changes;
            LoadChanges();
        }

        private void LoadChanges()
        {
            TotalChangesText.Text = $"{_allChanges.Count} Files";
            
            _viewModels = _allChanges.Select(c => new DeployFileViewModel(c)).ToList();
            FilesListBox.ItemsSource = _viewModels;
            FilesListBox.SelectedIndex = _viewModels.Count > 0 ? 0 : -1;
            UpdateDiffPreview();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectAllCheckBox.IsChecked == true)
            {
                foreach (var vm in _viewModels) vm.IsSelected = true;
            }
            else
            {
                foreach (var vm in _viewModels) vm.IsSelected = false;
            }
            FilesListBox.Items.Refresh();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            // Filter selected files
            var selectedVMs = _viewModels.Where(x => x.IsSelected).ToList();
            
            if (selectedVMs.Count == 0)
            {
                ModernMessageBox.Show("Please select at least one file to deploy.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Map back to FileChange
            SelectedFiles = _allChanges.Where(c => selectedVMs.Any(vm => vm.Name == c.Name)).ToList();
            
            Confirmed = true;
            this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDiffPreview();
        }

        private void UpdateDiffPreview()
        {
            if (CompareDiffViewer == null) return;

            if (FilesListBox?.SelectedItem is DeployFileViewModel vm)
            {
                CompareDiffViewer.Title = vm.Name;
                CompareDiffViewer.Status = vm.StatusText;
                CompareDiffViewer.FilePath = vm.Name;
                CompareDiffViewer.DiffText = vm.DiffText;
            }
            else
            {
                CompareDiffViewer.Title = "Diff preview";
                CompareDiffViewer.Status = string.Empty;
                CompareDiffViewer.FilePath = string.Empty;
                CompareDiffViewer.DiffText = string.Empty;
            }
        }

        private void FilesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FilesListBox?.SelectedItem is DeployFileViewModel vm)
            {
                OpenCodeViewer(vm);
            }
        }

        private void OpenCodeViewer(DeployFileViewModel vm)
        {
            try
            {
                var basePath = GitService.WorkingDirectoryPath;
                var relative = vm.Name.Replace('/', Path.DirectorySeparatorChar);
                var absolute = string.IsNullOrWhiteSpace(basePath) ? relative : Path.Combine(basePath, relative);
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
            if ((sender as FrameworkElement)?.Tag is DeployFileViewModel vm)
            {
                OpenCodeViewer(vm);
                e.Handled = true;
            }
        }
    }
}
