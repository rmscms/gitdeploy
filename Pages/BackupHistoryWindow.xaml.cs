using System.Collections.ObjectModel;
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
    }
}

