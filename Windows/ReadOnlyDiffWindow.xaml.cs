using System;
using System.Windows;

namespace GitDeployPro.Windows
{
    public partial class ReadOnlyDiffWindow : Window
    {
        private readonly string _diffText;

        public ReadOnlyDiffWindow(string filePath, string status, string diffText)
        {
            InitializeComponent();
            _diffText = diffText ?? string.Empty;

            TitleText.Text = string.IsNullOrWhiteSpace(filePath) ? "Diff Preview" : filePath;
            StatusText.Text = string.IsNullOrWhiteSpace(status) ? "MODIFIED" : status;

            DiffHost.Title = "Unified diff";
            DiffHost.Status = StatusText.Text;
            DiffHost.FilePath = filePath ?? string.Empty;
            DiffHost.DiffText = _diffText;
        }

        private void CopyDiff_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_diffText);
            }
            catch (Exception)
            {
                // Ignore clipboard issues.
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
