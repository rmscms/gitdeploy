using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Controls
{
    public partial class TerminalSuggestionsSettingsPanel : System.Windows.Controls.UserControl
    {
        private const string FilterAll = "All";
        private const string FilterGlobal = "Global";
        private const string FilterThisProject = "This project";
        private const string FilterOtherProjects = "Other projects";

        private List<TerminalSuggestion> _allSuggestions = new();
        private string? _currentProjectPath;
        private bool _suppressAutocompleteToggle;
        private bool _suppressDictionaryToggle;

        public TerminalSuggestionsSettingsPanel()
        {
            InitializeComponent();
            ScopeFilterComboBox.ItemsSource = new[] { FilterAll, FilterGlobal, FilterThisProject, FilterOtherProjects };
            ScopeFilterComboBox.SelectedIndex = 0;
            Loaded += (_, _) => Reload();
        }

        public void Reload(string? currentProjectPath = null)
        {
            if (!string.IsNullOrWhiteSpace(currentProjectPath))
            {
                _currentProjectPath = currentProjectPath;
            }
            else if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                _currentProjectPath = new ConfigurationService().LoadGlobalConfig().LastProjectPath;
            }

            UpdateCurrentProjectLabel();
            UpdateImportProjectScopeUi();

            _suppressAutocompleteToggle = true;
            _suppressDictionaryToggle = true;
            try
            {
                AutocompleteEnabledCheckBox.IsChecked = TerminalSuggestionStore.LoadAutocompleteEnabled();
                DictionaryModeCheckBox.IsChecked = TerminalSuggestionStore.LoadDictionaryModeEnabled();
            }
            finally
            {
                _suppressAutocompleteToggle = false;
                _suppressDictionaryToggle = false;
            }

            _allSuggestions = TerminalSuggestionStore.LoadAll();
            ApplyFilter();
        }

        private void UpdateCurrentProjectLabel()
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                CurrentProjectText.Text = "Current project: (none open)";
                return;
            }

            string name;
            try
            {
                name = Path.GetFileName(_currentProjectPath.TrimEnd('\\', '/'));
            }
            catch
            {
                name = _currentProjectPath;
            }

            CurrentProjectText.Text = $"Current project: {name}";
        }

        private void UpdateImportProjectScopeUi()
        {
            var hasProject = !string.IsNullOrWhiteSpace(_currentProjectPath);
            ImportProjectScopeRadio.IsEnabled = hasProject;
            if (!hasProject && ImportProjectScopeRadio.IsChecked == true)
            {
                ImportGlobalScopeRadio.IsChecked = true;
            }
        }

        private void ApplyFilter()
        {
            var rows = TerminalSuggestionStore.BuildSettingsRows(_currentProjectPath);
            var query = SearchTextBox?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(query))
            {
                rows = rows
                    .Where(r =>
                        r.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || r.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || r.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || r.ScopeLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var filter = ScopeFilterComboBox?.SelectedItem as string ?? FilterAll;
            rows = filter switch
            {
                FilterGlobal => rows.Where(r => r.IsGlobalScope).ToList(),
                FilterThisProject => rows.Where(r => r.IsCurrentProjectScope).ToList(),
                FilterOtherProjects => rows.Where(r => r.IsOtherProjectScope).ToList(),
                _ => rows
            };

            SuggestionsListView.ItemsSource = rows;
            StatusText.Text = $"{rows.Count} command(s)";
        }

        private TerminalSuggestionRowViewModel? GetSelectedRow()
            => SuggestionsListView.SelectedItem as TerminalSuggestionRowViewModel;

        private static TerminalSuggestionRowViewModel? GetRowFromListViewItem(System.Windows.Controls.ListViewItem? item)
            => item?.Content as TerminalSuggestionRowViewModel;

        private TerminalSuggestionRowViewModel? GetRowUnderMouse(MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not System.Windows.Controls.ListViewItem)
            {
                element = VisualTreeHelper.GetParent(element);
            }

            return GetRowFromListViewItem(element as System.Windows.Controls.ListViewItem);
        }

        private void PersistSuggestions()
        {
            TerminalSuggestionStore.SaveAll(_allSuggestions, AutocompleteEnabledCheckBox.IsChecked == true);
            Reload(_currentProjectPath);
        }

        private void AutocompleteEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutocompleteToggle)
            {
                return;
            }

            TerminalSuggestionStore.SaveAutocompleteEnabled(AutocompleteEnabledCheckBox.IsChecked == true);
        }

        private void DictionaryModeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressDictionaryToggle)
            {
                return;
            }

            TerminalSuggestionStore.SaveDictionaryModeEnabled(DictionaryModeCheckBox.IsChecked == true);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ScopeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void RowEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (SuggestionsListView.ItemsSource is not IEnumerable<TerminalSuggestionRowViewModel> rows)
            {
                return;
            }

            foreach (var row in rows)
            {
                var stored = _allSuggestions.FirstOrDefault(s => string.Equals(s.Id, row.Suggestion.Id, StringComparison.OrdinalIgnoreCase));
                if (stored != null)
                {
                    stored.IsEnabled = row.IsEnabled;
                }
            }

            TerminalSuggestionStore.SaveAll(_allSuggestions, AutocompleteEnabledCheckBox.IsChecked == true);
        }

        private void SuggestionsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = GetRowUnderMouse(e) ?? GetSelectedRow();
            if (row == null)
            {
                return;
            }

            SuggestionsListView.SelectedItem = row;
            OpenEditDialogForRow(row);
            e.Handled = true;
        }

        private void SuggestionsListView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var row = GetRowUnderMouse(e);
            if (row == null)
            {
                return;
            }

            SuggestionsListView.SelectedItem = row;
            ShowRowContextMenu(row, SuggestionsListView);
            e.Handled = true;
        }

        private void ShowRowContextMenu(TerminalSuggestionRowViewModel row, FrameworkElement target)
        {
            var actions = new List<AppContextMenuAction>
            {
                new()
                {
                    Id = "edit",
                    Label = "Edit…",
                    Execute = _ => OpenEditDialogForRow(row)
                },
                new()
                {
                    Id = "delete",
                    Label = "Delete",
                    IsDestructive = true,
                    Execute = _ => DeleteRow(row)
                },
                new()
                {
                    Id = "duplicate",
                    Label = "Duplicate",
                    Execute = _ => DuplicateRow(row)
                }
            };

            if (row.CanPromoteToGlobal)
            {
                actions.Add(new AppContextMenuAction
                {
                    Id = "make-global",
                    Label = "Make global",
                    Execute = _ => MakeGlobalRow(row)
                });
            }

            GlobalContextMenuService.ShowMenu(target, actions);
        }

        private void OpenEditDialogForRow(TerminalSuggestionRowViewModel row)
        {
            var dialog = new TerminalSuggestionDialog(_currentProjectPath, row.Suggestion)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            var index = _allSuggestions.FindIndex(s => string.Equals(s.Id, row.Suggestion.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _allSuggestions[index] = dialog.Result;
            }

            PersistSuggestions();
        }

        private void DeleteRow(TerminalSuggestionRowViewModel row)
        {
            _allSuggestions.RemoveAll(s => string.Equals(s.Id, row.Suggestion.Id, StringComparison.OrdinalIgnoreCase));
            PersistSuggestions();
        }

        private void DuplicateRow(TerminalSuggestionRowViewModel row)
        {
            var clone = row.Suggestion;
            _allSuggestions.Add(new TerminalSuggestion
            {
                Command = clone.Command,
                Description = clone.Description,
                Category = clone.Category,
                IsEnabled = clone.IsEnabled,
                Scope = clone.Scope,
                ProjectPath = clone.ProjectPath
            });
            PersistSuggestions();
        }

        private void MakeGlobalRow(TerminalSuggestionRowViewModel row)
        {
            if (!row.CanPromoteToGlobal)
            {
                return;
            }

            TerminalSuggestionStore.PromoteToGlobal(row.Suggestion.Id);
            Reload(_currentProjectPath);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TerminalSuggestionDialog(_currentProjectPath) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            _allSuggestions.Add(dialog.Result);
            PersistSuggestions();
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null)
            {
                return;
            }

            OpenEditDialogForRow(row);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null)
            {
                return;
            }

            DeleteRow(row);
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null)
            {
                return;
            }

            DuplicateRow(row);
        }

        private void MakeGlobalButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null)
            {
                return;
            }

            MakeGlobalRow(row);
        }

        private void RestoreLaravelButton_Click(object sender, RoutedEventArgs e)
        {
            var added = TerminalSuggestionStore.MergeLaravelDefaults();
            Reload(_currentProjectPath);
            StatusText.Text = added > 0
                ? $"Added {added} Laravel default command(s)."
                : "Laravel defaults already present.";
        }

        private void ImportBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import terminal commands",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ImportFilePathTextBox.Text = dialog.FileName;
            }
        }

        private void ImportCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ImportFilePathTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                StatusText.Text = "Select a JSON file to import.";
                return;
            }

            var scope = ImportProjectScopeRadio.IsChecked == true
                ? TerminalSuggestionScope.Project
                : TerminalSuggestionScope.Global;

            var result = TerminalSuggestionImportService.ImportFromFile(
                filePath,
                ImportCategoryTextBox.Text,
                scope,
                _currentProjectPath,
                ImportSkipDuplicatesCheckBox.IsChecked == true);

            if (result.Errors.Count > 0)
            {
                StatusText.Text = string.Join(" ", result.Errors);
                return;
            }

            Reload(_currentProjectPath);
            StatusText.Text =
                $"Added {result.Added} · Skipped {result.SkippedDuplicates} duplicates · {result.InvalidRows} invalid"
                + (string.IsNullOrWhiteSpace(result.ResolvedCategory) ? string.Empty : $" · category: {result.ResolvedCategory}");
        }

        private void OpenSampleJsonButton_Click(object sender, RoutedEventArgs e)
        {
            var samplePath = TerminalSuggestionImportService.GetSampleFilePath();
            if (!File.Exists(samplePath))
            {
                StatusText.Text = "Sample JSON file was not found in the output folder.";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = samplePath,
                UseShellExecute = true
            });
        }
    }
}
