using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitDeployPro.Models
{
    public class BackupTaskStatus : INotifyPropertyChanged
    {
        private BackupTaskState _state = BackupTaskState.Pending;
        private string _stage = "Preparing…";
        private int _processedTables;
        private int _totalTables;
        private string _currentTable = string.Empty;
        private long _currentTableProcessedRows;
        private long _currentTableTotalRows;
        private double _percent;
        private DateTime? _finishedLocal;
        private string _message = string.Empty;
        private bool _isCancelable;

        public Guid TaskId { get; set; } = Guid.NewGuid();
        public string ScheduleId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string ConnectionLabel { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public DateTime StartedLocal { get; set; } = DateTime.Now;

        public BackupTaskState State
        {
            get => _state;
            set => SetProperty(ref _state, value);
        }

        public string Stage
        {
            get => _stage;
            set => SetProperty(ref _stage, value);
        }

        public int ProcessedTables
        {
            get => _processedTables;
            set => SetProperty(ref _processedTables, value, nameof(ProcessedTables), nameof(ProgressSummary));
        }

        public int TotalTables
        {
            get => _totalTables;
            set => SetProperty(ref _totalTables, value, nameof(TotalTables), nameof(ProgressSummary));
        }

        public string ProgressSummary => TotalTables > 0
            ? $"{ProcessedTables}/{TotalTables} tables"
            : "Preparing…";

        public string CurrentTable
        {
            get => _currentTable;
            set => SetProperty(ref _currentTable, value);
        }

        public long CurrentTableProcessedRows
        {
            get => _currentTableProcessedRows;
            set => SetProperty(ref _currentTableProcessedRows, value, nameof(CurrentTableProgress));
        }

        public long CurrentTableTotalRows
        {
            get => _currentTableTotalRows;
            set => SetProperty(ref _currentTableTotalRows, value, nameof(CurrentTableProgress));
        }

        public string CurrentTableProgress
        {
            get
            {
                if (CurrentTableTotalRows > 0)
                {
                    return $"{CurrentTableProcessedRows:N0}/{CurrentTableTotalRows:N0} rows";
                }

                if (CurrentTableProcessedRows > 0)
                {
                    return $"{CurrentTableProcessedRows:N0} rows";
                }

                return string.Empty;
            }
        }

        public double Percent
        {
            get => _percent;
            set => SetProperty(ref _percent, value);
        }

        public DateTime? FinishedLocal
        {
            get => _finishedLocal;
            set => SetProperty(ref _finishedLocal, value);
        }

        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        public bool IsCancelable
        {
            get => _isCancelable;
            set => SetProperty(ref _isCancelable, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null, params string[] additional)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            if (additional != null)
            {
                foreach (var name in additional)
                {
                    OnPropertyChanged(name);
                }
            }
            return true;
        }

        protected void OnPropertyChanged(string? propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

