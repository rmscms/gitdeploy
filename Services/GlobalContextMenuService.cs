using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class GlobalContextMenuService
    {
        public static bool ShowMenu(
            FrameworkElement placementTarget,
            IEnumerable<AppContextMenuAction> actions,
            object? dataContext = null,
            PlacementMode placement = PlacementMode.MousePoint)
        {
            var visibleActions = (actions ?? Enumerable.Empty<AppContextMenuAction>())
                .Where(action => action != null && action.IsVisible)
                .ToList();

            if (placementTarget == null ||
                visibleActions.Count == 0 ||
                !visibleActions.Any(action => !action.IsSeparator))
            {
                return false;
            }

            var contextMenu = BuildMenu(placementTarget, visibleActions, dataContext, placement);
            placementTarget.ContextMenu = contextMenu;
            contextMenu.IsOpen = true;
            return true;
        }

        private static ContextMenu BuildMenu(
            FrameworkElement placementTarget,
            IReadOnlyList<AppContextMenuAction> actions,
            object? dataContext,
            PlacementMode placement)
        {
            var contextMenu = new ContextMenu
            {
                PlacementTarget = placementTarget,
                Placement = placement,
                DataContext = dataContext ?? placementTarget.DataContext
            };

            if (System.Windows.Application.Current?.TryFindResource("App.ContextMenu") is Style menuStyle)
            {
                contextMenu.Style = menuStyle;
            }

            AddActionsToItems(contextMenu.Items, actions);
            return contextMenu;
        }

        private static void AddActionsToItems(ItemCollection items, IReadOnlyList<AppContextMenuAction> actions)
        {
            var hasAnyAction = false;
            var hasPendingSeparator = false;
            foreach (var action in actions)
            {
                if (action.IsSeparator)
                {
                    if (hasAnyAction)
                    {
                        hasPendingSeparator = true;
                    }

                    continue;
                }

                if (hasPendingSeparator)
                {
                    var separator = new Separator();
                    if (System.Windows.Application.Current?.TryFindResource("App.ContextMenuSeparator") is Style separatorStyle)
                    {
                        separator.Style = separatorStyle;
                    }

                    items.Add(separator);
                    hasPendingSeparator = false;
                }

                var menuItem = CreateMenuItem(action);
                var children = action.Children?
                    .Where(child => child != null && child.IsVisible)
                    .ToList();
                if (children != null && children.Count > 0)
                {
                    AddActionsToItems(menuItem.Items, children);
                }

                items.Add(menuItem);
                hasAnyAction = true;
            }
        }

        private static MenuItem CreateMenuItem(AppContextMenuAction action)
        {
            var menuItem = new MenuItem
            {
                Header = action.Label,
                IsEnabled = action.IsEnabled,
                Tag = action.IsDestructive ? "danger" : null
            };

            if (System.Windows.Application.Current?.TryFindResource("App.ContextMenuItem") is Style menuItemStyle)
            {
                menuItem.Style = menuItemStyle;
            }

            if (!string.IsNullOrWhiteSpace(action.InputGestureText))
            {
                menuItem.InputGestureText = action.InputGestureText;
            }

            if (!string.IsNullOrWhiteSpace(action.IconGlyph))
            {
                menuItem.Icon = new TextBlock
                {
                    Text = action.IconGlyph,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var hasChildren = action.Children != null && action.Children.Any(c => c != null && c.IsVisible && !c.IsSeparator);
            if (hasChildren)
            {
                return menuItem;
            }

            if (action.Command != null)
            {
                menuItem.Command = action.Command;
                menuItem.CommandParameter = action.CommandParameter;
            }
            else if (action.Execute != null)
            {
                var callbackParameter = action.CommandParameter;
                menuItem.Click += (_, _) =>
                {
                    action.Execute(callbackParameter);
                };
            }

            return menuItem;
        }
    }
}
