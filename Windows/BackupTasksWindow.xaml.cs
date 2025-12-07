using System.Windows;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;
using Button = System.Windows.Controls.Button;

namespace GitDeployPro.Windows
{
    public partial class BackupTasksWindow : MetroWindow
    {
        private readonly BackupTaskMonitor _monitor = BackupTaskMonitor.Instance;

        public BackupTasksWindow()
        {
            InitializeComponent();
            DataContext = _monitor;
        }

        private void StopTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not BackupTaskStatus status)
            {
                return;
            }

            if (status.IsCancelable)
            {
                _monitor.CancelTask(status.TaskId);
            }
        }
    }
}

