using System;
using System.Linq;
using System.Windows;

namespace GitDeployPro.Services
{
    public static class WindowOwnerService
    {
        public static Window? ResolveOwner(DependencyObject? context = null, Window? preferredOwner = null)
        {
            if (IsUsableOwner(preferredOwner))
            {
                return preferredOwner;
            }

            if (context != null)
            {
                var contextOwner = Window.GetWindow(context);
                if (IsUsableOwner(contextOwner))
                {
                    return contextOwner;
                }
            }

            var app = System.Windows.Application.Current;
            if (app == null)
            {
                return null;
            }

            var activeWindow = app.Windows
                .OfType<Window>()
                .Where(IsUsableOwner)
                .FirstOrDefault(window => window.IsActive);
            if (activeWindow != null)
            {
                return activeWindow;
            }

            var main = app.MainWindow;
            if (IsUsableOwner(main))
            {
                return main;
            }

            return app.Windows
                .OfType<Window>()
                .Where(IsUsableOwner)
                .FirstOrDefault();
        }

        public static bool? ShowDialogOwned(
            Window dialog,
            DependencyObject? context = null,
            Window? preferredOwner = null,
            bool centerOnOwner = true)
        {
            if (dialog == null)
            {
                throw new ArgumentNullException(nameof(dialog));
            }

            ApplyOwner(dialog, ResolveOwner(context, preferredOwner), centerOnOwner);
            return dialog.ShowDialog();
        }

        public static void ShowOwned(
            Window window,
            DependencyObject? context = null,
            Window? preferredOwner = null,
            bool centerOnOwner = false)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            ApplyOwner(window, ResolveOwner(context, preferredOwner), centerOnOwner);
            window.Show();
        }

        public static void ApplyOwner(Window window, Window? owner, bool centerOnOwner = false)
        {
            if (window == null || owner == null || ReferenceEquals(window, owner))
            {
                return;
            }

            RestoreIfMinimized(owner);
            window.Owner = owner;
            if (centerOnOwner && window.WindowStartupLocation != WindowStartupLocation.Manual)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }

        public static void RestoreIfMinimized(Window? window)
        {
            if (window == null)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.Activate();
        }

        public static bool IsOnScreen(double left, double top)
        {
            return !double.IsNaN(left)
                   && !double.IsNaN(top)
                   && left > -2000
                   && top > -2000;
        }

        private static bool IsUsableOwner(Window? window)
        {
            return window != null
                   && window.IsLoaded
                   && window.IsVisible
                   && window.WindowState != WindowState.Minimized;
        }
    }
}
