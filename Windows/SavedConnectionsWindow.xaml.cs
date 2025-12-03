using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitDeployPro.Models;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class SavedConnectionsWindow : Window
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<DatabaseConnectionEntry> _connections = new();

        public DatabaseConnectionEntry? SelectedConnection { get; private set; }
        public bool ShouldConnect { get; private set; }

        public SavedConnectionsWindow()
        {
            InitializeComponent();
            ConnectionsList.ItemsSource = _connections;
            LoadConnections();
        }

        private void LoadConnections()
        {
            _connections.Clear();

            // Add localhost as first option
            _connections.Add(DatabaseConnectionEntry.CreateLocalDefault());

            // Load saved profiles with database info
            var profiles = _configService.LoadConnections();
            foreach (var profile in profiles)
            {
                if (profile.DbType == DatabaseType.None) continue;
                _connections.Add(DatabaseConnectionEntry.FromProfile(profile));
            }

            var hasProfiles = _connections.Count > 1;
            EmptyState.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
            ConnectionsList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is DatabaseConnectionEntry entry)
            {
                SelectedConnection = entry;
                ShouldConnect = true;
                DialogResult = true;
                Close();
            }
        }

        private void ManageButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ConnectionManagerWindow { Owner = this };
            window.ShowDialog();
            LoadConnections();
        }
    }
}

