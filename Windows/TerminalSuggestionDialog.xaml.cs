using System;
using System.Windows;
using System.Windows.Input;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class TerminalSuggestionDialog : MahApps.Metro.Controls.MetroWindow
    {
        private readonly string? _currentProjectPath;
        private readonly TerminalSuggestion? _existing;

        public TerminalSuggestion? Result { get; private set; }

        public TerminalSuggestionDialog(string? currentProjectPath, TerminalSuggestion? existing = null, string? prefillCommand = null)
        {
            InitializeComponent();
            _currentProjectPath = currentProjectPath;
            _existing = existing;

            Title = existing == null ? "Add terminal command" : "Edit terminal command";
            CategoryComboBox.ItemsSource = new[]
            {
                TerminalSuggestionCatalog.CategoryLaravel,
                TerminalSuggestionCatalog.CategoryNavigation,
                TerminalSuggestionCatalog.CategoryCustom
            };

            UpdateProjectScopeUi();
            if (existing != null)
            {
                CommandTextBox.Text = existing.Command;
                DescriptionTextBox.Text = existing.Description;
                CategoryComboBox.Text = existing.Category;
                if (existing.Scope == TerminalSuggestionScope.Project)
                {
                    ProjectScopeRadio.IsChecked = true;
                    GlobalScopeRadio.IsChecked = false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(prefillCommand))
            {
                CommandTextBox.Text = prefillCommand.Trim();
                CategoryComboBox.SelectedIndex = 2;
                if (!string.IsNullOrWhiteSpace(_currentProjectPath))
                {
                    ProjectScopeRadio.IsChecked = true;
                    GlobalScopeRadio.IsChecked = false;
                }
            }
            else
            {
                CategoryComboBox.SelectedIndex = 2;
            }

            Loaded += (_, _) => CommandTextBox.Focus();
        }

        private void UpdateProjectScopeUi()
        {
            var hasProject = !string.IsNullOrWhiteSpace(_currentProjectPath);
            ProjectScopeRadio.IsEnabled = hasProject;
            if (!hasProject)
            {
                GlobalScopeRadio.IsChecked = true;
                ProjectScopeRadio.IsChecked = false;
                ProjectScopeHint.Text = "Open a project first to scope a command to one project.";
                return;
            }

            string name;
            try
            {
                name = System.IO.Path.GetFileName(_currentProjectPath!.TrimEnd('\\', '/'));
            }
            catch
            {
                name = _currentProjectPath!;
            }

            ProjectScopeRadio.Content = $"This project only — {name}";
            ProjectScopeHint.Text = $"Stored for: {_currentProjectPath}";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var command = CommandTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                ModernMessageBox.Show("Command is required.", "Terminal command", MessageBoxButton.OK, MessageBoxImage.Warning, owner: this);
                CommandTextBox.Focus();
                return;
            }

            var useProject = ProjectScopeRadio.IsChecked == true;
            if (useProject && string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                ModernMessageBox.Show("Open a project before saving a project-scoped command.", "Terminal command", MessageBoxButton.OK, MessageBoxImage.Warning, owner: this);
                return;
            }

            var category = CategoryComboBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                category = TerminalSuggestionCatalog.CategoryCustom;
            }

            Result = new TerminalSuggestion
            {
                Id = _existing?.Id ?? Guid.NewGuid().ToString(),
                Command = command,
                Description = DescriptionTextBox.Text?.Trim() ?? string.Empty,
                Category = category,
                IsEnabled = _existing?.IsEnabled ?? true,
                Scope = useProject ? TerminalSuggestionScope.Project : TerminalSuggestionScope.Global,
                ProjectPath = useProject ? _currentProjectPath ?? string.Empty : string.Empty
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
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OkButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
