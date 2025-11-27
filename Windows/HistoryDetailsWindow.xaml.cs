using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Services; // For DeploymentRecord

namespace GitDeployPro.Windows
{
    public partial class HistoryDetailsWindow : Window
    {
        public HistoryDetailsWindow(DeploymentRecord record)
        {
            InitializeComponent();
            LoadDetails(record);
        }

        private void LoadDetails(DeploymentRecord record)
        {
            if (record == null) return;

            StatusText.Text = record.Status;
            BranchText.Text = string.IsNullOrEmpty(record.Branch) ? "N/A" : record.Branch;
            DateText.Text = record.Date.ToString("yyyy/MM/dd HH:mm");

            if (record.Status == "Failed" || record.Status.Contains("Fail"))
            {
                StatusIcon.Text = "❌";
                StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 115, 115));
            }
            else
            {
                StatusIcon.Text = "✅";
                StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
            }

            FilesListContainer.Children.Clear();
            var files = record.Files ?? new List<string>();
            
            // If no files (e.g. from commit history without file list), show a message or fetch if possible.
            // For now, if empty, we might want to show "No file details available" or just list them if available.
            // Since commit history build in HistoryPage initializes Files as empty list, this will be empty for git history items.
            // But for real deployments, it will have files.
            
            if (files.Count == 0)
            {
                 var p = new TextBlock 
                 { 
                     Text = "No file details available for this record.", 
                     Foreground = System.Windows.Media.Brushes.Gray,
                     FontStyle = FontStyles.Italic,
                     Margin = new Thickness(5)
                 };
                 FilesListContainer.Children.Add(p);
            }
            else
            {
                foreach (var file in files)
                {
                    var p = new Border
                    {
                        Padding = new Thickness(10),
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = new StackPanel
                        {
                            Orientation = System.Windows.Controls.Orientation.Horizontal,
                            Children =
                            {
                                new TextBlock { Text = "📄", Margin = new Thickness(0, 0, 10, 0), Foreground = System.Windows.Media.Brushes.Gray },
                                new TextBlock { Text = file, Foreground = System.Windows.Media.Brushes.LightGray }
                            }
                        }
                    };
                    FilesListContainer.Children.Add(p);
                }
            }
        }

        private void Rollback_Click(object sender, RoutedEventArgs e)
        {
            var confirm = ModernMessageBox.Show("Are you sure you want to rollback to this version? This will overwrite current files.", "Confirm Rollback", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm)
            {
                ModernMessageBox.Show("Rollback feature is coming soon!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
