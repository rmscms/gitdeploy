using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Theme;

namespace GitDeployPro
{
    public partial class App : System.Windows.Application
    {
        private const string LogFileName = "GitDeployPro.log";
        private readonly BackupSchedulerRunner _schedulerRunner = BackupSchedulerRunner.Instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            PerformanceSampler.Instance.Mark("app", "lifecycle", "startup-begin");
            ConfigureUnhandledExceptions();
            // Unfreeze palette brushes BEFORE StartupUri windows resolve StaticResource.
            ThemeService.Instance.Initialize();
            try
            {
                var savedThemeId = new ConfigurationService().ResolveAppThemeId();
                ThemeService.Instance.ApplyTheme(savedThemeId);
            }
            catch
            {
                ThemeService.Instance.ApplyTheme(ThemeService.DarkThemeId);
            }

            LocalizationService.Instance.InitializeFromConfig();

            base.OnStartup(e);
            LocalizationService.Instance.ApplyFlowDirection();
            RegisterGlobalMouseWheelScrolling();
            Log("Application started.");
            _schedulerRunner.Start();
            PerformanceSampler.Instance.Mark("app", "lifecycle", "startup-end");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            PerformanceSampler.Instance.Mark("app", "lifecycle", "exit-begin");
            _schedulerRunner.Dispose();
            base.OnExit(e);
            PerformanceSampler.Instance.Mark("app", "lifecycle", "exit-end");
        }

        private void ConfigureUnhandledExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                HandleException(ex.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException");

            AppDomain.CurrentDomain.FirstChanceException += (s, ex) =>
                HandleFirstChanceException(ex.Exception);

            this.DispatcherUnhandledException += (s, ex) =>
            {
                HandleException(ex.Exception, "Application.DispatcherUnhandledException");
                ex.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                HandleException(ex.Exception, "TaskScheduler.UnobservedTaskException");
                ex.SetObserved();
            };
        }

        private static void RegisterGlobalMouseWheelScrolling()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(HandleGlobalPreviewMouseWheel),
                handledEventsToo: true);
        }

        private static void HandleGlobalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject origin)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                return;
            }

            var target = FindScrollableViewer(origin, e.Delta);
            if (target == null)
            {
                return;
            }

            ScrollViewerByDelta(target, e.Delta);
            e.Handled = true;
        }

        private static ScrollViewer? FindScrollableViewer(DependencyObject origin, int delta)
        {
            ScrollViewer? fallback = null;
            DependencyObject? current = origin;

            while (current != null)
            {
                if (current is ScrollViewer viewer && viewer.ScrollableHeight > 0)
                {
                    fallback ??= viewer;
                    if (CanScrollInDirection(viewer, delta))
                    {
                        return viewer;
                    }
                }

                current = GetParent(current);
            }

            return fallback;
        }

        private static bool CanScrollInDirection(ScrollViewer viewer, int delta)
        {
            if (delta > 0)
            {
                return viewer.VerticalOffset > 0;
            }

            if (delta < 0)
            {
                return viewer.VerticalOffset < viewer.ScrollableHeight;
            }

            return false;
        }

        private static void ScrollViewerByDelta(ScrollViewer viewer, int delta)
        {
            if (delta == 0)
            {
                return;
            }

            var notchCount = Math.Max(1, Math.Abs(delta) / Mouse.MouseWheelDeltaForOneLine);
            var wheelLines = SystemParameters.WheelScrollLines;

            if (wheelLines <= 0)
            {
                for (var i = 0; i < notchCount; i++)
                {
                    if (delta > 0)
                    {
                        viewer.PageUp();
                    }
                    else
                    {
                        viewer.PageDown();
                    }
                }

                return;
            }

            var lineCount = wheelLines * notchCount;
            for (var i = 0; i < lineCount; i++)
            {
                if (delta > 0)
                {
                    viewer.LineUp();
                }
                else
                {
                    viewer.LineDown();
                }
            }
        }

        private static DependencyObject? GetParent(DependencyObject child)
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

        private void HandleException(Exception? exception, string source)
        {
            if (exception == null) return;

            if (IsBenignCancellation(exception) || IsBenignConnectivity(exception))
            {
                // Connectivity / cancel noise stays in the log + in-app warnings — not a crash dialog.
                var quiet = $"[{AppTimeService.LocalNow:yyyy-MM-dd HH:mm:ss}] [{source}] (benign) {exception.GetType().Name}: {exception.Message}";
                Log(quiet);
                return;
            }

            var message = $"[{AppTimeService.LocalNow:yyyy-MM-dd HH:mm:ss}] [{source}] {exception.Message}\n{exception.StackTrace}";
            Log(message);
            PerformanceSampler.Instance.Mark("app", "unhandled-exception", source, exception: exception);

            Controls.ModernMessageBox.Show(Loc.T("msg.unexpectedError"), Loc.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void HandleFirstChanceException(Exception? exception)
        {
            if (exception == null) return;

            var message = $"[{AppTimeService.LocalNow:yyyy-MM-dd HH:mm:ss}] [FirstChance] {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}";
            Log(message);
        }

        private bool IsBenignCancellation(Exception exception)
        {
            if (exception is OperationCanceledException || exception is TaskCanceledException)
            {
                return true;
            }

            if (exception is AggregateException aggregate)
            {
                aggregate = aggregate.Flatten();
                return aggregate.InnerExceptions.All(IsBenignCancellation);
            }

            return false;
        }

        /// <summary>
        /// Expected when MySQL/SSH/network is offline — UI already shows warnings; do not popup Error.
        /// </summary>
        private static bool IsBenignConnectivity(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                aggregate = aggregate.Flatten();
                return aggregate.InnerExceptions.Count > 0
                       && aggregate.InnerExceptions.All(IsBenignConnectivity);
            }

            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException || current is TaskCanceledException)
                {
                    return true;
                }

                if (current is TimeoutException
                    || current is SocketException
                    || current is IOException
                    || current is HttpRequestException
                    || current.GetType().FullName == "MySqlConnector.MySqlException"
                    || current.GetType().Name is "MySqlException" or "SshConnectionException" or "SshOperationTimeoutException")
                {
                    return true;
                }

                // UnobservedTaskException wrapper text often nests the real fault.
                if (current.Message.Contains("Unable to connect to any of the specified MySQL hosts", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("A connection attempt failed", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Log(string message)
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, LogFileName);
                File.AppendAllText(logPath, message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
