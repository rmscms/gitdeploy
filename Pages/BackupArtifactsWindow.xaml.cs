using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls;

namespace GitDeployPro.Pages
{
    public partial class BackupArtifactsWindow : MetroWindow, INotifyPropertyChanged
    {
        private readonly BackupSchedulerPage _ownerPage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public BackupArtifactsWindow(BackupSchedulerPage ownerPage)
        {
            _ownerPage = ownerPage;
            InitializeComponent();
            DataContext = this;
            RefreshFromSource();
        }

        public System.Collections.ObjectModel.ObservableCollection<BackupSchedulerPage.ProjectBackupFileItem> ProjectBackupFiles
            => _ownerPage.ProjectBackupFiles;

        public BackupSchedulerPage.ProjectBackupFileItem? SelectedProjectBackupFile
        {
            get => _ownerPage.SelectedProjectBackupFile;
            set => _ownerPage.SelectedProjectBackupFile = value;
        }

        public string ArtifactFilesPathLabel => _ownerPage.ArtifactFilesPathLabel;

        public string ArtifactFilesStatus => _ownerPage.ArtifactFilesStatus;

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ownerPage.RefreshArtifactsForWindow();
            RefreshFromSource();
        }

        private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            _ownerPage.OpenArtifactExplorerFromWindow();
        }

        private void RefreshFromSource()
        {
            OnPropertyChanged(nameof(ProjectBackupFiles));
            OnPropertyChanged(nameof(SelectedProjectBackupFile));
            OnPropertyChanged(nameof(ArtifactFilesPathLabel));
            OnPropertyChanged(nameof(ArtifactFilesStatus));
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
