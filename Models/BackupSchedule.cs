using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitDeployPro.Models
{
    public enum BackupScheduleFrequency
    {
        Once,
        Daily,
        Weekly,
        Monthly,
        CustomInterval
    }

    public enum BackupCompressionFormat
    {
        Zip,
        TarGz
    }

    public class BackupSchedule : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        private string _name = "New backup schedule";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string _connectionProfileId = string.Empty;
        public string ConnectionProfileId
        {
            get => _connectionProfileId;
            set => SetProperty(ref _connectionProfileId, value);
        }

        private string _databaseName = string.Empty;
        public string DatabaseName
        {
            get => _databaseName;
            set => SetProperty(ref _databaseName, value);
        }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }

        private BackupScheduleFrequency _frequency = BackupScheduleFrequency.Daily;
        public BackupScheduleFrequency Frequency
        {
            get => _frequency;
            set => SetProperty(ref _frequency, value);
        }

        private TimeSpan _localRunTime = TimeSpan.FromHours(2);
        public TimeSpan LocalRunTime
        {
            get => _localRunTime;
            set => SetProperty(ref _localRunTime, value);
        }

        private List<DayOfWeek> _daysOfWeek = new List<DayOfWeek> { DayOfWeek.Monday };
        public List<DayOfWeek> DaysOfWeek
        {
            get => _daysOfWeek;
            set => SetProperty(ref _daysOfWeek, value);
        }

        private int _dayOfMonth = 1;
        public int DayOfMonth
        {
            get => _dayOfMonth;
            set => SetProperty(ref _dayOfMonth, value);
        }

        private int _customIntervalMinutes = 1440;
        public int CustomIntervalMinutes
        {
            get => _customIntervalMinutes;
            set => SetProperty(ref _customIntervalMinutes, value);
        }

        private string _outputDirectory = string.Empty;
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => SetProperty(ref _outputDirectory, value);
        }

        private bool _compressOutput = true;
        public bool CompressOutput
        {
            get => _compressOutput;
            set => SetProperty(ref _compressOutput, value);
        }

        private BackupCompressionFormat _compressionFormat = BackupCompressionFormat.Zip;
        public BackupCompressionFormat CompressionFormat
        {
            get => _compressionFormat;
            set => SetProperty(ref _compressionFormat, value);
        }

        private int _retentionCount = 10;
        public int RetentionCount
        {
            get => _retentionCount;
            set => SetProperty(ref _retentionCount, value);
        }

        private BackupMode _backupMode = BackupMode.Standard;
        public BackupMode BackupMode
        {
            get => _backupMode;
            set => SetProperty(ref _backupMode, value);
        }

        private DateTime? _lastRunUtc;
        public DateTime? LastRunUtc
        {
            get => _lastRunUtc;
            set => SetProperty(ref _lastRunUtc, value);
        }

        private DateTime? _nextRunUtc;
        public DateTime? NextRunUtc
        {
            get => _nextRunUtc;
            set
            {
                if (SetProperty(ref _nextRunUtc, value))
                {
                    OnPropertyChanged(nameof(NextRunLocal));
                }
            }
        }

        public DateTime? NextRunLocal => _nextRunUtc?.ToLocalTime();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum BackupMode
    {
        Standard,
        Fast,
        ExternalTool
    }
}

