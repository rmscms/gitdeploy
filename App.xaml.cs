using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace GitDeployPro
{
    public partial class App : System.Windows.Application
    {
        private const string LogFileName = "GitDeployPro.log";

        protected override void OnStartup(StartupEventArgs e)
        {
            ConfigureUnhandledExceptions();
            base.OnStartup(e);
            Log("Application started.");
        }

        private void ConfigureUnhandledExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                HandleException(ex.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException");

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

            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exception.Message}\n{exception.StackTrace}";
            Log(message);

            System.Windows.MessageBox.Show("An unexpected error occurred. Details saved to log file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
