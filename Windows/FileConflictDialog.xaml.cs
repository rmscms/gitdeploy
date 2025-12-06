using System.ComponentModel;
using System.Windows;

namespace GitDeployPro.Windows
{
    public enum FileConflictChoice
    {
        Overwrite,
        Skip,
        OverwriteAll,
        SkipAll,
        Cancel
    }

    public partial class FileConflictDialog : MahApps.Metro.Controls.MetroWindow
    {
        public FileConflictChoice Choice { get; private set; } = FileConflictChoice.Cancel;

        public FileConflictDialog(string path)
        {
            InitializeComponent();
            FilePathText.Text = path;
        }

        private void Overwrite_Click(object sender, RoutedEventArgs e)
        {
            Choice = FileConflictChoice.Overwrite;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Choice = FileConflictChoice.Skip;
            DialogResult = true;
        }

        private void OverwriteAll_Click(object sender, RoutedEventArgs e)
        {
            Choice = FileConflictChoice.OverwriteAll;
            DialogResult = true;
        }

        private void SkipAll_Click(object sender, RoutedEventArgs e)
        {
            Choice = FileConflictChoice.SkipAll;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = FileConflictChoice.Cancel;
            DialogResult = false;
        }

        private void FileConflictDialog_Closing(object? sender, CancelEventArgs e)
        {
            if (Choice != FileConflictChoice.Cancel)
            {
                return;
            }

            DialogResult = false;
        }
    }
}

