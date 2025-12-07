using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Services;

namespace GitDeployPro
{
    public partial class App : System.Windows.Application
    {
        private const string LogFileName = "GitDeployPro.log";
        private readonly BackupSchedulerRunner _schedulerRunner = BackupSchedulerRunner.Instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            ConfigureUnhandledExceptions();
            base.OnStartup(e);
            Log("Application started.");
            _schedulerRunner.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _schedulerRunner.Dispose();
            base.OnExit(e);
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

        private void HandleException(Exception? exception, string source)
        {
            if (exception == null) return;

            if (IsBenignCancellation(exception))
            {
                return;
            }

            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exception.Message}\n{exception.StackTrace}";
            Log(message);

            System.Windows.MessageBox.Show("An unexpected error occurred. Details saved to log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void HandleFirstChanceException(Exception? exception)
        {
            if (exception == null) return;

            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FirstChance] {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}";
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
