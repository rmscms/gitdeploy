using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GitDeployPro.Behaviors
{
    internal static class DependencyObjectAncestors
    {
        public static T? Find<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T matched)
                {
                    return matched;
                }

                current = GetParent(current);
            }

            return null;
        }

        public static DependencyObject? GetParent(DependencyObject child)
        {
            if (child is Popup popup)
            {
                return popup.PlacementTarget;
            }

            if (child is Visual || child is Visual3D)
            {
                var visualParent = VisualTreeHelper.GetParent(child);
                if (visualParent != null)
                {
                    return visualParent;
                }
            }

            return LogicalTreeHelper.GetParent(child);
        }
    }
}
