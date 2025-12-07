using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public sealed class BackupTaskMonitor : INotifyPropertyChanged
    {
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<Guid, TaskRegistration> _registrations = new();

        private class TaskRegistration
        {
            public BackupTaskStatus Status { get; init; } = null!;
            public CancellationTokenSource Cancellation { get; init; } = new();
            public PauseTokenSource? PauseSource { get; set; }
        }

        public static BackupTaskMonitor Instance { get; } = new BackupTaskMonitor();

        public ObservableCollection<BackupTaskStatus> ActiveTasks { get; } = new();
        public ObservableCollection<BackupTaskStatus> RecentTasks { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<string, bool>? TaskLogCreated;

        public int ActiveCount => ActiveTasks.Count;

        private BackupTaskMonitor()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public BackupTaskHandle StartTask(BackupSchedule schedule, ConnectionProfile profile, bool allowCancel, string origin)
        {
            var status = new BackupTaskStatus
            {
                ScheduleId = schedule.Id,
                ScheduleName = schedule.Name,
                DatabaseName = schedule.DatabaseName,
                ConnectionLabel = profile.DisplayName ?? profile.Name ?? profile.Host,
                Mode = schedule.BackupMode.ToString(),
                Origin = origin,
                State = BackupTaskState.Running,
                IsCancelable = allowCancel,
                StartedLocal = DateTime.Now
            };

            var registration = new TaskRegistration
            {
                Status = status,
                Cancellation = new CancellationTokenSource()
            };

            lock (_registrations)
            {
                _registrations[status.TaskId] = registration;
            }

            _dispatcher.Invoke(() =>
            {
                ActiveTasks.Insert(0, status);
                OnPropertyChanged(nameof(ActiveCount));
            });

            TaskLogCreated?.Invoke($"Started backup '{status.ScheduleName}' ({status.Mode}).", false);

            return new BackupTaskHandle(status.TaskId, registration.Cancellation);
        }

        public void AttachPauseToken(Guid taskId, PauseTokenSource pauseSource)
        {
            lock (_registrations)
            {
                if (_registrations.TryGetValue(taskId, out var reg))
                {
                    reg.PauseSource = pauseSource;
                }
            }
        }

        public void UpdateProgress(Guid taskId, BackupProgressUpdate update)
        {
            TaskRegistration? registration;
            lock (_registrations)
            {
                _registrations.TryGetValue(taskId, out registration);
            }

            if (registration == null) return;

            _dispatcher.Invoke(() =>
            {
                var status = registration.Status;
                if (update.TotalTables > 0)
                {
                    status.TotalTables = update.TotalTables;
                }
                if (update.ProcessedTables >= 0)
                {
                    status.ProcessedTables = update.ProcessedTables;
                }

                if (!string.IsNullOrWhiteSpace(update.Stage))
                {
                    status.Stage = update.Stage;
                }
                else if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    status.Stage = update.Message;
                }

                if (!string.IsNullOrWhiteSpace(update.CurrentTable))
                {
                    status.CurrentTable = update.CurrentTable;
                }
                status.CurrentTableProcessedRows = update.CurrentTableProcessedRows;
                status.CurrentTableTotalRows = update.CurrentTableTotalRows;

                if (status.TotalTables > 0)
                {
                    status.Percent = Math.Clamp((double)status.ProcessedTables / status.TotalTables * 100d, 0d, 100d);
                }
            });
        }

        public void CompleteTask(Guid taskId, string message)
        {
            FinishTask(taskId, BackupTaskState.Completed, message, false);
        }

        public void FailTask(Guid taskId, string message)
        {
            FinishTask(taskId, BackupTaskState.Failed, message, true);
        }

        public void CancelTask(Guid taskId)
        {
            TaskRegistration? registration;
            lock (_registrations)
            {
                _registrations.TryGetValue(taskId, out registration);
            }

            if (registration == null) return;
            registration.Status.IsCancelable = false;
            registration.Status.Stage = "Cancel requested…";
            registration.Cancellation.Cancel();
            registration.PauseSource?.Resume();
            TaskLogCreated?.Invoke($"Cancel requested for '{registration.Status.ScheduleName}'.", false);
        }

        public void MarkCancelled(Guid taskId, string message)
        {
            FinishTask(taskId, BackupTaskState.Cancelled, message, false);
        }

        private void FinishTask(Guid taskId, BackupTaskState finalState, string message, bool isError)
        {
            TaskRegistration? registration;
            lock (_registrations)
            {
                if (!_registrations.TryGetValue(taskId, out registration))
                {
                    return;
                }
                _registrations.Remove(taskId);
            }

            _dispatcher.Invoke(() =>
            {
                var status = registration.Status;
                status.State = finalState;
                status.IsCancelable = false;
                status.Stage = message;
                status.Message = message;
                status.FinishedLocal = DateTime.Now;
                status.Percent = 100;

                ActiveTasks.Remove(status);
                OnPropertyChanged(nameof(ActiveCount));

                RecentTasks.Insert(0, status);
                while (RecentTasks.Count > 50)
                {
                    RecentTasks.RemoveAt(RecentTasks.Count - 1);
                }
            });

            TaskLogCreated?.Invoke(message, isError);
        }

        private void OnPropertyChanged(string? propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class BackupTaskHandle : IDisposable
    {
        public Guid TaskId { get; }
        public CancellationTokenSource Cancellation { get; }

        internal BackupTaskHandle(Guid taskId, CancellationTokenSource cancellation)
        {
            TaskId = taskId;
            Cancellation = cancellation;
        }

        public void Dispose()
        {
            Cancellation.Dispose();
        }
    }
}

