using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitDeployPro.Models;

namespace GitDeployPro.Windows
{
    public partial class NewConnectionWindow : Window
    {
        public DatabaseConnectionEntry? ResultConnection { get; private set; }
        public bool IsConnected { get; private set; }

        public NewConnectionWindow()
        {
            InitializeComponent();
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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
            var username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "root" : UsernameBox.Text.Trim();

            if (!int.TryParse(PortBox.Text, out var port))
                port = 3306;

            var dbType = DatabaseType.MySQL;
            if (DbTypeCombo.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "MariaDB")
                dbType = DatabaseType.MariaDB;

            ResultConnection = new DatabaseConnectionEntry
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{host}:{port}",
                DbType = dbType,
                Host = host,
                Port = port,
                Username = username,
                Password = PasswordBox.Password,
                DatabaseName = DatabaseBox.Text?.Trim() ?? string.Empty,
                Description = $"{username}@{host}:{port}",
                IsLocal = host == "127.0.0.1" || host.ToLower() == "localhost"
            };

            IsConnected = true;
            DialogResult = true;
            Close();
        }
    }
}

