using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Pages;

namespace GitDeployPro
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Load Dashboard by default
            ContentFrame.Navigate(new DashboardPage());
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new DashboardPage());
        }

        private void Deploy_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new DeployPage());
        }
        
        private void Git_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new GitPage());
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new HistoryPage());
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new SettingsPage());
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
