using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Remote;

namespace GitDeployPro.Windows
{
    public partial class TempTargetPickerWindow
    {
        public sealed class Row : INotifyPropertyChanged
        {
            private bool _isChecked;

            public int Index { get; init; }
            public string Name { get; init; } = "";
            public string RemotePath { get; init; } = "";

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked != value)
                    {
                        _isChecked = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                    }
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly List<Row> _rows;
        private readonly bool _multi;

        /// <summary>Indices (into the target list) the user ticked, in list order.</summary>
        public IReadOnlyList<int> SelectedIndices => _rows.Where(r => r.IsChecked).Select(r => r.Index).ToList();

        /// <param name="multi">true = tick any number of targets; false = pick exactly one.</param>
        public TempTargetPickerWindow(IReadOnlyList<TemporaryTarget> targets, int currentIndex, bool multi)
        {
            InitializeComponent();
            _multi = multi;
            _rows = targets.Select((t, i) => new Row { Index = i, Name = t.Name, RemotePath = t.RemotePath, IsChecked = !multi && i == currentIndex }).ToList();
            TargetsList.ItemsSource = _rows;
            HeadingText.Text = Loc.T(multi ? "tmap.pick.headingMulti" : "tmap.pick.headingSingle");
            HintText.Text = Loc.T(multi ? "tmap.pick.hintMulti" : "tmap.pick.hintSingle");
            MultiTools.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            UpdateState();
        }

        private void UpdateState()
        {
            var n = _rows.Count(r => r.IsChecked);
            CountText.Text = Loc.T("tmap.pick.count", n, _rows.Count);
            OkButton.Content = _multi ? Loc.T("tmap.pick.okMulti", n) : Loc.T("tmap.pick.okSingle");
            OkButton.IsEnabled = n > 0;
        }

        private void PickOnly(Row row)
        {
            foreach (var r in _rows)
            {
                r.IsChecked = ReferenceEquals(r, row);
            }
        }

        private void Check_Click(object sender, RoutedEventArgs e)
        {
            if (!_multi && sender is FrameworkElement { Tag: Row row })
            {
                PickOnly(row);
            }

            UpdateState();
        }

        private void Row_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Clicking the card toggles it (the checkbox handles its own click).
            if (e.OriginalSource is DependencyObject d && IsInsideCheckBox(d))
            {
                return;
            }

            if (sender is FrameworkElement { Tag: Row row })
            {
                if (_multi)
                {
                    row.IsChecked = !row.IsChecked;
                }
                else
                {
                    PickOnly(row);
                }

                UpdateState();
            }
        }

        private static bool IsInsideCheckBox(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.CheckBox)
                {
                    return true;
                }

                d = VisualTreeHelper.GetParent(d);
            }

            return false;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows)
            {
                r.IsChecked = true;
            }

            UpdateState();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows)
            {
                r.IsChecked = false;
            }

            UpdateState();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
