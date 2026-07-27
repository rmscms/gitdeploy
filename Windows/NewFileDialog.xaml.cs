using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GitDeployPro.Controls;

namespace GitDeployPro.Windows
{
    public partial class NewFileDialog
    {
        public string FileName { get; private set; } = string.Empty;

        public NewFileDialog(string? defaultName = "newfile", string? defaultExtension = "txt")
        {
            InitializeComponent();
            NameTextBox.Text = string.IsNullOrWhiteSpace(defaultName) ? "newfile" : defaultName.Trim();
            ExtensionTextBox.Text = NormalizeExtensionInput(defaultExtension ?? "txt");
            NameTextBox.SelectAll();
            NameTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var name = (NameTextBox.Text ?? string.Empty).Trim();
            var extension = NormalizeExtension(ExtensionTextBox.Text);

            if (string.IsNullOrWhiteSpace(name))
            {
                ModernMessageBox.Show("Enter a file name.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.Contains('/')
                || name.Contains('\\'))
            {
                ModernMessageBox.Show("File name cannot contain path separators or invalid characters.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If user already typed an extension in the name, keep it unless extension field is set.
            if (name.Contains('.') && string.IsNullOrWhiteSpace(ExtensionTextBox.Text))
            {
                FileName = name;
            }
            else if (name.Contains('.'))
            {
                // Prefer explicit extension field when provided.
                var baseName = Path.GetFileNameWithoutExtension(name);
                FileName = string.IsNullOrEmpty(extension) ? baseName : baseName + extension;
            }
            else
            {
                FileName = string.IsNullOrEmpty(extension) ? name : name + extension;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Input_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
            }
        }

        private static string NormalizeExtensionInput(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim().TrimStart('.');
            return string.IsNullOrWhiteSpace(trimmed) ? "txt" : trimmed;
        }

        private static string NormalizeExtension(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            trimmed = trimmed.TrimStart('.');
            return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : "." + trimmed;
        }
    }
}
