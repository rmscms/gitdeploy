using System.Collections.Generic;
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
            FilesItemsControl.ItemsSource = _viewModels;
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
            FilesItemsControl.ItemsSource = null;
            FilesItemsControl.ItemsSource = _viewModels;
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
    }
}
