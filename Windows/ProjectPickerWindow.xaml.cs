using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class ProjectPickerWindow
    {
        private readonly List<ProjectRow> _all = new();

        public string? SelectedPath { get; private set; }

        public ProjectPickerWindow(IEnumerable<ConfigurationService.RecentProjectEntry> projects)
        {
            InitializeComponent();

            var source = projects ?? Enumerable.Empty<ConfigurationService.RecentProjectEntry>();
            foreach (var entry in source
                         .Where(p => !string.IsNullOrWhiteSpace(p.Path))
                         .OrderByDescending(p => p.LastOpenedUtc))
            {
                var name = System.IO.Path.GetFileName(entry.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = entry.Path;
                }

                _all.Add(new ProjectRow(
                    name,
                    entry.Path,
                    GetInitial(name),
                    GetColor(name)));
            }

            ApplyFilter(string.Empty);
            Loaded += (_, _) =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            };
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyFilter(SearchBox.Text);

        private void ApplyFilter(string? query)
        {
            var needle = (query ?? string.Empty).Trim();
            IEnumerable<ProjectRow> filtered = _all;
            if (!string.IsNullOrWhiteSpace(needle))
            {
                filtered = _all.Where(p =>
                    p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || p.Path.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();
            ProjectsList.ItemsSource = list;
            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (list.Count > 0)
            {
                ProjectsList.SelectedIndex = 0;
            }
        }

        private void ProjectsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
        }

        private void ProjectsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => TryAccept();

        private void OpenButton_Click(object sender, RoutedEventArgs e)
            => TryAccept();

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TryAccept()
        {
            if (ProjectsList.SelectedItem is not ProjectRow row
                || string.IsNullOrWhiteSpace(row.Path))
            {
                return;
            }

            SelectedPath = row.Path;
            DialogResult = true;
            Close();
        }

        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TryAccept();
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private static string GetInitial(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "?";
            }

            return name.Substring(0, 1).ToUpperInvariant();
        }

        private static SolidColorBrush GetColor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return System.Windows.Media.Brushes.Gray;
            }

            var colors = new[]
            {
                "#3574F0", "#E05555", "#579A57", "#E59500", "#9B59B6", "#00ACC1", "#F06292"
            };
            var index = Math.Abs(name.GetHashCode()) % colors.Length;
            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[index]));
            brush.Freeze();
            return brush;
        }

        private sealed class ProjectRow
        {
            public ProjectRow(string name, string path, string initial, SolidColorBrush colorBrush)
            {
                Name = name;
                Path = path;
                Initial = initial;
                ColorBrush = colorBrush;
            }

            public string Name { get; }
            public string Path { get; }
            public string Initial { get; }
            public SolidColorBrush ColorBrush { get; }
        }
    }
}
