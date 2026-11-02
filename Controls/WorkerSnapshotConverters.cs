using System;
using System.Globalization;
using System.Windows.Data;

namespace GitDeployPro.Controls
{
    /// <summary>Maps free-form worker status strings ("Busy", "Error", "Waiting"...) to WorkerState.</summary>
    public sealed class WorkerStateStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Contains("error") || s.Contains("fail")) return WorkerState.Failed;
            if (s.Contains("done") || s.Contains("complete") || s.Contains("success")) return WorkerState.Done;
            if (s.Contains("pause")) return WorkerState.Paused;
            if (s.Contains("wait") || s.Contains("idle") || s.Contains("queue") || s.Length == 0) return WorkerState.Queued;
            return WorkerState.Running;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Running workers show a partial bar, done shows 100, others 0.</summary>
    public sealed class WorkerProgressConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var state = (WorkerState)new WorkerStateStringConverter().Convert(value, targetType, parameter, culture);
            return state switch
            {
                WorkerState.Done => 100.0,
                WorkerState.Running => 60.0,
                _ => 0.0
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Maps BackupTaskState to WorkerState.</summary>
    public sealed class BackupTaskStateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value as GitDeployPro.Models.BackupTaskState?) switch
            {
                GitDeployPro.Models.BackupTaskState.Running => WorkerState.Running,
                GitDeployPro.Models.BackupTaskState.Paused => WorkerState.Paused,
                GitDeployPro.Models.BackupTaskState.Completed => WorkerState.Done,
                GitDeployPro.Models.BackupTaskState.Failed => WorkerState.Failed,
                GitDeployPro.Models.BackupTaskState.Cancelled => WorkerState.Failed,
                _ => WorkerState.Queued
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
