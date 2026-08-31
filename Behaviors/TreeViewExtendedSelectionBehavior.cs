using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GitDeployPro.Models;
using GitDeployPro.Services;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using CheckBox = System.Windows.Controls.CheckBox;

namespace GitDeployPro.Behaviors
{
    /// <summary>
    /// Windows Explorer-style multi-select on a TreeView whose items implement <see cref="ITreeMultiSelectable"/>.
    /// </summary>
    public static class TreeViewExtendedSelectionBehavior
    {
        public static readonly DependencyProperty EnableProperty =
            DependencyProperty.RegisterAttached(
                "Enable",
                typeof(bool),
                typeof(TreeViewExtendedSelectionBehavior),
                new PropertyMetadata(false, OnEnableChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(SelectionState),
                typeof(TreeViewExtendedSelectionBehavior));

        public static void SetEnable(DependencyObject element, bool value) =>
            element.SetValue(EnableProperty, value);

        public static bool GetEnable(DependencyObject element) =>
            (bool)element.GetValue(EnableProperty);

        public static IReadOnlyList<T> GetSelectedItems<T>(TreeView tree) where T : class, ITreeMultiSelectable
        {
            if (tree == null)
            {
                return Array.Empty<T>();
            }

            return EnumerateAll(tree)
                .OfType<T>()
                .Where(item => item.IsMultiSelected)
                .ToList();
        }

        public static void ApplyRightClickSelection(TreeView tree, ITreeMultiSelectable item)
        {
            if (tree == null || item == null || !item.IncludeInMultiSelect)
            {
                return;
            }

            if (!item.IsMultiSelected)
            {
                SelectExclusive(tree, item);
            }

            SetFocusItem(tree, item);
        }

        public static void ClearSelection(TreeView tree)
        {
            if (tree == null)
            {
                return;
            }

            var state = GetState(tree);
            foreach (var item in EnumerateAll(tree))
            {
                item.IsMultiSelected = false;
            }

            state.Selected.Clear();
            state.Anchor = null;
        }

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TreeView tree)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                tree.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                tree.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
                tree.PreviewKeyDown += OnPreviewKeyDown;
            }
            else
            {
                tree.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                tree.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
                tree.PreviewKeyDown -= OnPreviewKeyDown;
                tree.ClearValue(StateProperty);
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeView tree)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (source == null || ShouldIgnoreMouseSource(source))
            {
                return;
            }

            EditorKeyboardScope.Disarm();

            var treeItem = FindAncestor<TreeViewItem>(source);
            if (treeItem?.DataContext is not ITreeMultiSelectable item || !item.IncludeInMultiSelect)
            {
                if (treeItem == null)
                {
                    ClearSelection(tree);
                    e.Handled = true;
                }

                return;
            }

            if (e.ClickCount > 1)
            {
                return;
            }

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (shift)
            {
                SelectRange(tree, item, addToExisting: ctrl);
            }
            else if (ctrl)
            {
                Toggle(tree, item);
            }
            else
            {
                SelectExclusive(tree, item);
            }

            SetFocusItem(tree, item, treeItem);
            e.Handled = true;
        }

        private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeView tree)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            var treeItem = FindAncestor<TreeViewItem>(source);
            if (treeItem?.DataContext is not ITreeMultiSelectable item || !item.IncludeInMultiSelect)
            {
                return;
            }

            ApplyRightClickSelection(tree, item);
            SetFocusItem(tree, item, treeItem);
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TreeView tree || !tree.IsKeyboardFocusWithin)
            {
                return;
            }

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                if (TreeNameSearch.GetSearchActive(tree))
                {
                    return;
                }

                ClearSelection(tree);
                e.Handled = true;
                return;
            }

            if (key == Key.A && ctrl && !shift)
            {
                SelectAllVisible(tree);
                e.Handled = true;
                return;
            }

            var visible = EnumerateVisible(tree).ToList();
            if (visible.Count == 0)
            {
                return;
            }

            var state = GetState(tree);
            var focused = state.Focused ?? visible.FirstOrDefault(item => item.IsMultiSelected) ?? visible[0];
            var index = Math.Max(0, visible.IndexOf(focused));

            if (key == Key.Space)
            {
                if (ctrl)
                {
                    Toggle(tree, focused);
                }
                else
                {
                    SelectExclusive(tree, focused);
                }

                SetFocusItem(tree, focused);
                e.Handled = true;
                return;
            }

            if (key is not Key.Up and not Key.Down and not Key.Home and not Key.End)
            {
                return;
            }

            var delta = key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.Home => -index,
                Key.End => visible.Count - 1 - index,
                _ => 0
            };

            var nextIndex = Math.Clamp(index + delta, 0, visible.Count - 1);
            var next = visible[nextIndex];

            if (ctrl && !shift)
            {
                SetFocusItem(tree, next);
                e.Handled = true;
                return;
            }

            if (shift)
            {
                SelectRange(tree, next, addToExisting: ctrl);
            }
            else
            {
                SelectExclusive(tree, next);
            }

            SetFocusItem(tree, next);
            e.Handled = true;
        }

        private static void SelectExclusive(TreeView tree, ITreeMultiSelectable item)
        {
            var state = GetState(tree);
            foreach (var node in EnumerateAll(tree))
            {
                node.IsMultiSelected = ReferenceEquals(node, item);
            }

            state.Selected.Clear();
            state.Selected.Add(item);
            item.IsMultiSelected = true;
            state.Anchor = item;
        }

        private static void Toggle(TreeView tree, ITreeMultiSelectable item)
        {
            var state = GetState(tree);
            item.IsMultiSelected = !item.IsMultiSelected;
            if (item.IsMultiSelected)
            {
                state.Selected.Add(item);
            }
            else
            {
                state.Selected.Remove(item);
            }
        }

        private static void SelectRange(TreeView tree, ITreeMultiSelectable target, bool addToExisting)
        {
            var state = GetState(tree);
            var visible = EnumerateVisible(tree).ToList();
            var anchor = state.Anchor ?? state.Focused ?? target;
            var start = visible.IndexOf(anchor);
            var end = visible.IndexOf(target);
            if (start < 0)
            {
                start = end;
            }

            if (end < 0)
            {
                return;
            }

            if (start > end)
            {
                (start, end) = (end, start);
            }

            if (!addToExisting)
            {
                foreach (var item in EnumerateAll(tree))
                {
                    item.IsMultiSelected = false;
                }

                state.Selected.Clear();
            }

            for (var i = start; i <= end; i++)
            {
                var item = visible[i];
                item.IsMultiSelected = true;
                state.Selected.Add(item);
            }

            if (state.Anchor == null)
            {
                state.Anchor = anchor;
            }
        }

        private static void SelectAllVisible(TreeView tree)
        {
            var state = GetState(tree);
            state.Selected.Clear();
            ITreeMultiSelectable? first = null;
            foreach (var item in EnumerateVisible(tree))
            {
                first ??= item;
                item.IsMultiSelected = true;
                state.Selected.Add(item);
            }

            state.Anchor ??= first;
        }

        private static void SetFocusItem(TreeView tree, ITreeMultiSelectable item, TreeViewItem? container = null)
        {
            var state = GetState(tree);
            state.Focused = item;
            container ??= FindContainer(tree, item);
            if (container == null)
            {
                return;
            }

            container.IsSelected = true;
            container.Focus();
            container.BringIntoView();
        }

        private static IEnumerable<ITreeMultiSelectable> EnumerateVisible(TreeView tree)
        {
            return Enumerate(tree.ItemsSource ?? tree.Items, expandedOnly: true);
        }

        private static IEnumerable<ITreeMultiSelectable> EnumerateAll(TreeView tree)
        {
            return Enumerate(tree.ItemsSource ?? tree.Items, expandedOnly: false);
        }

        private static IEnumerable<ITreeMultiSelectable> Enumerate(IEnumerable? items, bool expandedOnly)
        {
            if (items == null)
            {
                yield break;
            }

            foreach (var raw in items)
            {
                if (raw is not ITreeMultiSelectable node)
                {
                    continue;
                }

                if (node.IncludeInMultiSelect)
                {
                    yield return node;
                }

                if (expandedOnly && !node.IsExpanded)
                {
                    continue;
                }

                foreach (var child in Enumerate(node.Children, expandedOnly))
                {
                    yield return child;
                }
            }
        }

        private static TreeViewItem? FindContainer(ItemsControl parent, object item)
        {
            if (parent.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            {
                parent.UpdateLayout();
            }

            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem match)
            {
                return match;
            }

            foreach (var child in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childItem)
                {
                    continue;
                }

                var nested = FindContainer(childItem, item);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool ShouldIgnoreMouseSource(DependencyObject source)
        {
            if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null
                || FindAncestor<System.Windows.Controls.Primitives.Thumb>(source) != null)
            {
                return true;
            }

            if (FindAncestor<CheckBox>(source) != null)
            {
                return true;
            }

            var toggle = FindAncestor<ToggleButton>(source);
            return toggle != null;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
            => DependencyObjectAncestors.Find<T>(current);

        private static SelectionState GetState(TreeView tree)
        {
            if (tree.GetValue(StateProperty) is SelectionState existing)
            {
                return existing;
            }

            var created = new SelectionState();
            tree.SetValue(StateProperty, created);
            return created;
        }

        private sealed class SelectionState
        {
            public ITreeMultiSelectable? Anchor { get; set; }
            public ITreeMultiSelectable? Focused { get; set; }
            public HashSet<ITreeMultiSelectable> Selected { get; } = new(ReferenceEqualityComparer.Instance);
        }
    }

    public static class TreeMultiSelectHelpers
    {
        public static IReadOnlyList<T> CollapseNestedByPath<T>(
            IEnumerable<T> items,
            Func<T, string> getPath,
            Func<T, bool> isDirectory,
            char separator = '/')
        {
            var list = items
                .Where(item => !string.IsNullOrWhiteSpace(getPath(item)))
                .Select(item =>
                {
                    var path = NormalizePath(getPath(item), separator);
                    if (isDirectory(item))
                    {
                        path = path.TrimEnd(separator);
                    }

                    return (Item: item, Path: path, IsDirectory: isDirectory(item));
                })
                .OrderBy(entry => entry.Path.Length)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var kept = new List<T>();
            var keptDirs = new List<string>();
            foreach (var entry in list)
            {
                var nested = keptDirs.Any(parent =>
                    entry.Path.Equals(parent, StringComparison.OrdinalIgnoreCase)
                    || entry.Path.StartsWith(parent + separator, StringComparison.OrdinalIgnoreCase));
                if (nested)
                {
                    continue;
                }

                kept.Add(entry.Item);
                if (entry.IsDirectory)
                {
                    keptDirs.Add(entry.Path);
                }
            }

            return kept;
        }

        private static string NormalizePath(string path, char separator)
        {
            var trimmed = path.Replace('\\', separator).Replace('/', separator).TrimEnd(separator);
            return string.IsNullOrWhiteSpace(trimmed) ? separator.ToString() : trimmed;
        }
    }
}
