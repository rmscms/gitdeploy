using System.Collections.ObjectModel;
using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Pages
{
    public partial class BackupHistoryWindow : MetroWindow
    {
        public ObservableCollection<BackupHistoryEntry> History { get; } = BackupHistoryStore.LoadHistory();

        public BackupHistoryWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void ClearBackupHistory_Click(object sender, RoutedEventArgs e)
        {
            if (History.Count == 0)
            {
                ModernMessageBox.Show("Backup history is already empty.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = ModernMessageBox.ShowWithResult(
                "Are you sure you want to clear all backup history records?",
                "Clear backup history",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Clear",
                "Cancel");

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            BackupHistoryStore.ClearHistory();
            History.Clear();
            ModernMessageBox.Show("Backup history cleared.", "Backup history", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

