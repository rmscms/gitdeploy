using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfTextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;

namespace GitDeployPro.Behaviors
{
    public static class ComboBoxSearchBehavior
    {
        private sealed class SearchState
        {
            public bool IsWired;
            public bool SuppressTextEvents;
            public bool PreviousTextSearchEnabled = true;
            public ICollectionView? View;
            public Predicate<object>? BaseFilter;
            public WpfTextBox? SearchBox;
            public FrameworkElement? SearchHost;
        }

        public static readonly DependencyProperty EnableAutoSearchProperty =
            DependencyProperty.RegisterAttached(
                "EnableAutoSearch",
                typeof(bool),
                typeof(ComboBoxSearchBehavior),
                new PropertyMetadata(false, OnEnableAutoSearchChanged));

        public static readonly DependencyProperty SearchThresholdProperty =
            DependencyProperty.RegisterAttached(
                "SearchThreshold",
                typeof(int),
                typeof(ComboBoxSearchBehavior),
                new PropertyMetadata(10));

        private static readonly DependencyProperty SearchStateProperty =
            DependencyProperty.RegisterAttached(
                "SearchState",
                typeof(SearchState),
                typeof(ComboBoxSearchBehavior),
                new PropertyMetadata(null));

        public static bool GetEnableAutoSearch(DependencyObject element)
            => (bool)element.GetValue(EnableAutoSearchProperty);

        public static void SetEnableAutoSearch(DependencyObject element, bool value)
            => element.SetValue(EnableAutoSearchProperty, value);

        public static int GetSearchThreshold(DependencyObject element)
            => (int)element.GetValue(SearchThresholdProperty);

        public static void SetSearchThreshold(DependencyObject element, int value)
            => element.SetValue(SearchThresholdProperty, value);

        private static SearchState GetOrCreateState(WpfComboBox comboBox)
        {
            var state = comboBox.GetValue(SearchStateProperty) as SearchState;
            if (state != null)
            {
                return state;
            }

            state = new SearchState();
            comboBox.SetValue(SearchStateProperty, state);
            return state;
        }

        private static void OnEnableAutoSearchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not WpfComboBox comboBox)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                Wire(comboBox);
            }
            else
            {
                Unwire(comboBox);
            }
        }

        private static void Wire(WpfComboBox comboBox)
        {
            var state = GetOrCreateState(comboBox);
            if (state.IsWired)
            {
                return;
            }

            comboBox.DropDownOpened += ComboBox_DropDownOpened;
            comboBox.DropDownClosed += ComboBox_DropDownClosed;
            comboBox.Unloaded += ComboBox_Unloaded;
            comboBox.PreviewMouseLeftButtonDown += ComboBox_PreviewMouseLeftButtonDown;
            state.IsWired = true;
        }

        private static void Unwire(WpfComboBox comboBox)
        {
            if (comboBox.GetValue(SearchStateProperty) is not SearchState state || !state.IsWired)
            {
                return;
            }

            comboBox.DropDownOpened -= ComboBox_DropDownOpened;
            comboBox.DropDownClosed -= ComboBox_DropDownClosed;
            comboBox.Unloaded -= ComboBox_Unloaded;
            comboBox.PreviewMouseLeftButtonDown -= ComboBox_PreviewMouseLeftButtonDown;
            CleanupSearch(comboBox, state);
            DetachSearchBoxHandler(state);
            state.IsWired = false;
        }

        private static void ComboBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not WpfComboBox comboBox)
            {
                return;
            }

            if (comboBox.GetValue(SearchStateProperty) is SearchState state)
            {
                CleanupSearch(comboBox, state);
                DetachSearchBoxHandler(state);
            }
        }

        private static void ComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not WpfComboBox comboBox || !comboBox.IsEnabled)
            {
                return;
            }

            if (!comboBox.IsDropDownOpen)
            {
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                e.Handled = true;
            }
        }

        private static void ComboBox_DropDownOpened(object? sender, EventArgs e)
        {
            if (sender is not WpfComboBox comboBox)
            {
                return;
            }

            var state = GetOrCreateState(comboBox);
            EnsureTemplateParts(comboBox, state);

            if (state.SearchHost == null || state.SearchBox == null)
            {
                return;
            }

            var threshold = Math.Max(1, GetSearchThreshold(comboBox));
            var shouldSearch = comboBox.Items.Count > threshold;
            state.SearchHost.Visibility = shouldSearch ? Visibility.Visible : Visibility.Collapsed;

            if (!shouldSearch)
            {
                CleanupSearch(comboBox, state);
                return;
            }

            state.View = CollectionViewSource.GetDefaultView(comboBox.ItemsSource ?? comboBox.Items);
            if (state.View != null)
            {
                state.BaseFilter = state.View.Filter;
            }

            state.PreviousTextSearchEnabled = comboBox.IsTextSearchEnabled;
            comboBox.IsTextSearchEnabled = false;

            DetachSearchBoxHandler(state);
            state.SearchBox.TextChanged += SearchBox_TextChanged;
            state.SearchBox.Tag = comboBox;

            state.SuppressTextEvents = true;
            state.SearchBox.Text = string.Empty;
            state.SuppressTextEvents = false;

            comboBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!comboBox.IsDropDownOpen || state.SearchBox == null)
                {
                    return;
                }

                state.SearchBox.Focus();
            }), DispatcherPriority.Input);
        }

        private static void ComboBox_DropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not WpfComboBox comboBox)
            {
                return;
            }

            if (comboBox.GetValue(SearchStateProperty) is not SearchState state)
            {
                return;
            }

            CleanupSearch(comboBox, state);
        }

        private static void EnsureTemplateParts(WpfComboBox comboBox, SearchState state)
        {
            comboBox.ApplyTemplate();

            state.SearchHost = comboBox.Template.FindName("PART_SearchHost", comboBox) as FrameworkElement;
            state.SearchBox = comboBox.Template.FindName("PART_SearchBox", comboBox) as WpfTextBox;
        }

        private static void CleanupSearch(WpfComboBox comboBox, SearchState state)
        {
            comboBox.IsTextSearchEnabled = state.PreviousTextSearchEnabled;

            if (state.View != null)
            {
                state.View.Filter = state.BaseFilter;
                state.View.Refresh();
            }

            if (state.SearchBox != null)
            {
                state.SuppressTextEvents = true;
                state.SearchBox.Text = string.Empty;
                state.SuppressTextEvents = false;
            }
        }

        private static void DetachSearchBoxHandler(SearchState state)
        {
            if (state.SearchBox != null)
            {
                state.SearchBox.TextChanged -= SearchBox_TextChanged;
                state.SearchBox.Tag = null;
            }
        }

        private static void SearchBox_TextChanged(object sender, WpfTextChangedEventArgs e)
        {
            if (sender is not WpfTextBox searchBox || searchBox.Tag is not WpfComboBox comboBox)
            {
                return;
            }

            if (comboBox.GetValue(SearchStateProperty) is not SearchState state || state.SuppressTextEvents || state.View == null)
            {
                return;
            }

            var query = (searchBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                state.View.Filter = state.BaseFilter;
                state.View.Refresh();
                return;
            }

            state.View.Filter = item =>
            {
                var basePass = state.BaseFilter?.Invoke(item) ?? true;
                if (!basePass)
                {
                    return false;
                }

                return MatchItem(item, query, comboBox.DisplayMemberPath);
            };
            state.View.Refresh();
        }

        private static bool MatchItem(object? item, string query, string? displayMemberPath)
        {
            if (item == null)
            {
                return false;
            }

            if (item is string textItem)
            {
                return textItem.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(displayMemberPath))
            {
                var descriptor = TypeDescriptor.GetProperties(item).Find(displayMemberPath, true);
                if (descriptor != null)
                {
                    var value = descriptor.GetValue(item)?.ToString() ?? string.Empty;
                    if (value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (item is WpfComboBoxItem comboBoxItem)
            {
                var contentText = comboBoxItem.Content?.ToString() ?? string.Empty;
                if (contentText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var text = item.ToString() ?? string.Empty;
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // For object collections with ItemTemplate (no DisplayMemberPath),
            // scan common scalar properties like Name/Host/Title/... so search works predictably.
            var properties = TypeDescriptor.GetProperties(item);
            foreach (PropertyDescriptor property in properties)
            {
                if (property == null || !property.IsBrowsable)
                {
                    continue;
                }

                var value = property.GetValue(item);
                if (value == null || !IsSearchableValue(value))
                {
                    continue;
                }

                var valueText = value.ToString() ?? string.Empty;
                if (valueText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSearchableValue(object value)
        {
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }

            return value is string ||
                   value is decimal ||
                   value is DateTime ||
                   value is DateTimeOffset ||
                   value is TimeSpan ||
                   value is Guid;
        }
    }
}
