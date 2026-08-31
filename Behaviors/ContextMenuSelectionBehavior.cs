using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitDeployPro.Models;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace GitDeployPro.Behaviors
{
    public static class ContextMenuSelectionBehavior
    {
        public static readonly DependencyProperty EnableProperty =
            DependencyProperty.RegisterAttached(
                "Enable",
                typeof(bool),
                typeof(ContextMenuSelectionBehavior),
                new PropertyMetadata(false, OnEnableChanged));

        public static void SetEnable(DependencyObject element, bool value) =>
            element.SetValue(EnableProperty, value);

        public static bool GetEnable(DependencyObject element) =>
            (bool)element.GetValue(EnableProperty);

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.ItemsControl itemsControl)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                itemsControl.PreviewMouseRightButtonDown += ItemsControlOnPreviewMouseRightButtonDown;
            }
            else
            {
                itemsControl.PreviewMouseRightButtonDown -= ItemsControlOnPreviewMouseRightButtonDown;
            }
        }

        private static void ItemsControlOnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.ItemsControl itemsControl)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (source == null)
            {
                return;
            }

            if (itemsControl is TreeView treeView)
            {
                var treeViewItem = FindAncestor<TreeViewItem>(source);
                if (treeViewItem?.DataContext is ITreeMultiSelectable selectable)
                {
                    TreeViewExtendedSelectionBehavior.ApplyRightClickSelection(treeView, selectable);
                    treeViewItem.Focus();
                    return;
                }

                if (treeViewItem != null)
                {
                    treeViewItem.IsSelected = true;
                    treeViewItem.Focus();
                }

                return;
            }

            if (itemsControl is System.Windows.Controls.ListView)
            {
                var listViewItem = FindAncestor<System.Windows.Controls.ListViewItem>(source);
                if (listViewItem != null)
                {
                    listViewItem.IsSelected = true;
                    listViewItem.Focus();
                }

                return;
            }

            if (itemsControl is System.Windows.Controls.ListBox)
            {
                var listBoxItem = FindAncestor<System.Windows.Controls.ListBoxItem>(source);
                if (listBoxItem != null)
                {
                    listBoxItem.IsSelected = true;
                    listBoxItem.Focus();
                }
            }
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
            => DependencyObjectAncestors.Find<T>(current);
    }
}
