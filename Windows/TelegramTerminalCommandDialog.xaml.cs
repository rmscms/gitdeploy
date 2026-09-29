using System;
using System.Windows;
using System.Windows.Input;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class TelegramTerminalCommandDialog : MahApps.Metro.Controls.MetroWindow
    {
        private readonly string? _projectPath;
        private readonly TelegramTerminalCommand? _existing;

        public TelegramTerminalCommand? Result { get; private set; }

        public TelegramTerminalCommandDialog(string? projectPath, TelegramTerminalCommand? existing = null, TelegramTerminalCommand? prefill = null)
        {
            InitializeComponent();
            _projectPath = projectPath;
            _existing = existing;

            Title = existing == null ? "Add Telegram terminal preset" : "Edit Telegram terminal preset";
            UpdateProjectHint();

            var source = existing ?? prefill;
            if (source != null)
            {
                DescriptionTextBox.Text = source.Description;
                CommandTextBox.Text = source.Command;
                NotifyBeforeRunCheckBox.IsChecked = source.NotifyBeforeRun;
                NotifyMessageTextBox.Text = source.NotifyMessage;
            }

            Loaded += (_, _) => DescriptionTextBox.Focus();
        }

        private void UpdateProjectHint()
        {
            if (string.IsNullOrWhiteSpace(_projectPath))
            {
                ProjectHintText.Text = "Open a project first — presets are stored per project.";
                return;
            }

            ProjectHintText.Text = $"Stored for project: {_projectPath}";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_projectPath))
            {
                ModernMessageBox.Show(
                    "Open a project before saving a Telegram terminal preset.",
                    "Telegram terminal preset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    owner: this);
                return;
            }

            var command = CommandTextBox.Text?.Replace("\r\n", " ").Replace('\n', ' ').Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                ModernMessageBox.Show("Command is required.", "Telegram terminal preset", MessageBoxButton.OK, MessageBoxImage.Warning, owner: this);
                CommandTextBox.Focus();
                return;
            }

            var description = DescriptionTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(description))
            {
                ModernMessageBox.Show("Button label is required.", "Telegram terminal preset", MessageBoxButton.OK, MessageBoxImage.Warning, owner: this);
                DescriptionTextBox.Focus();
                return;
            }

            Result = new TelegramTerminalCommand
            {
                Id = _existing?.Id ?? Guid.NewGuid().ToString(),
                Description = description,
                Command = command,
                ProjectPath = TelegramTerminalCommandStore.NormalizePath(_projectPath),
                NotifyBeforeRun = NotifyBeforeRunCheckBox.IsChecked == true,
                NotifyMessage = NotifyMessageTextBox.Text?.Trim() ?? string.Empty
            };

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
            if (e.Key == System.Windows.Input.Key.Enter && sender != CommandTextBox)
            {
                OkButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
