using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Telegram;
using GitDeployPro.Windows;

namespace GitDeployPro.Controls
{
    public partial class TelegramTerminalCommandsPanel : System.Windows.Controls.UserControl
    {
        private readonly ObservableCollection<TelegramTerminalCommandRow> _rows = new();
        private string? _currentProjectPath;

        public TelegramTerminalCommandsPanel()
        {
            InitializeComponent();
            CommandsListView.ItemsSource = _rows;
        }

        public void Reload(string? projectPath)
        {
            _currentProjectPath = string.IsNullOrWhiteSpace(projectPath)
                ? null
                : TelegramTerminalCommandStore.NormalizePath(projectPath);

            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                CurrentProjectText.Text = "Current project: (none) — open a project to manage presets.";
            }
            else
            {
                var name = System.IO.Path.GetFileName(_currentProjectPath.TrimEnd('\\', '/'));
                CurrentProjectText.Text = $"Current project: {name}\n{_currentProjectPath}";
            }

            var canEdit = !string.IsNullOrWhiteSpace(_currentProjectPath);
            AddButton.IsEnabled = canEdit;
            GoBuildTemplateButton.IsEnabled = canEdit;
            EditButton.IsEnabled = canEdit;
            DeleteButton.IsEnabled = canEdit;

            _rows.Clear();
            foreach (var item in TelegramTerminalCommandStore.ListForSettings(_currentProjectPath))
            {
                _rows.Add(new TelegramTerminalCommandRow(item));
            }
        }

        private TelegramTerminalCommandRow? SelectedRow()
            => CommandsListView.SelectedItem as TelegramTerminalCommandRow;

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            OpenDialog(existing: null, prefill: null);
        }

        private void GoBuildTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                return;
            }

            OpenDialog(existing: null, prefill: TelegramTerminalCommandCatalog.CreateGoBuildExcore(_currentProjectPath));
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow();
            if (row == null)
            {
                return;
            }

            OpenDialog(row.Command, null);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow();
            if (row == null)
            {
                return;
            }

            var all = TelegramTerminalCommandStore.LoadAll();
            var removed = all.RemoveAll(c =>
                string.Equals(c.Id, row.Command.Id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return;
            }

            TelegramTerminalCommandStore.SaveAll(all);
            Reload(_currentProjectPath);
        }

        private void OpenDialog(TelegramTerminalCommand? existing, TelegramTerminalCommand? prefill)
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                ModernMessageBox.Show(
                    "Open a project before adding Telegram terminal presets.",
                    "Telegram terminal preset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    owner: Window.GetWindow(this));
                return;
            }

            var dialog = new TelegramTerminalCommandDialog(_currentProjectPath, existing, prefill)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            var all = TelegramTerminalCommandStore.LoadAll();
            var index = all.FindIndex(c =>
                string.Equals(c.Id, dialog.Result.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                all[index] = dialog.Result;
            }
            else
            {
                var count = all.Count(c =>
                    TelegramTerminalCommandStore.PathsMatch(c.ProjectPath, _currentProjectPath));
                if (count >= TelegramTerminalCommandStore.MaxCommandsPerProject)
                {
                    ModernMessageBox.Show(
                        $"Maximum {TelegramTerminalCommandStore.MaxCommandsPerProject} presets per project.",
                        "Telegram terminal preset",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        owner: Window.GetWindow(this));
                    return;
                }

                all.Add(dialog.Result);
            }

            TelegramTerminalCommandStore.SaveAll(all);
            Reload(_currentProjectPath);
        }

        private sealed class TelegramTerminalCommandRow
        {
            public TelegramTerminalCommandRow(TelegramTerminalCommand command)
            {
                Command = command;
            }

            public TelegramTerminalCommand Command { get; }

            public string Description => Command.Description;

            public string CommandPreview
            {
                get
                {
                    var text = Command.Command ?? string.Empty;
                    return text.Length <= 80 ? text : text[..77] + "…";
                }
            }

            public string NotifyLabel => Command.NotifyBeforeRun ? "yes" : "no";
        }
    }
}
