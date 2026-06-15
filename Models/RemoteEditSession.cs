using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitDeployPro.Services;

namespace GitDeployPro.Models
{
    public sealed class RemoteEditSession : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        private string _fileName = string.Empty;
        private string _content = string.Empty;
        private string _workingContent = string.Empty;
        private string _originalContentHash = string.Empty;
        private RemoteFileStat? _originalStat;
        private DateTime _loadedAtUtc = AppTimeService.UtcNow;
        private bool _isDirty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string FilePath
        {
            get => _filePath;
            set => SetField(ref _filePath, value);
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (SetField(ref _fileName, value))
                {
                    OnPropertyChanged(nameof(TabLabel));
                }
            }
        }

        // Last uploaded/saved content from remote point of view.
        public string Content
        {
            get => _content;
            set => SetField(ref _content, value);
        }

        // Current editor buffer for this tab/session.
        public string WorkingContent
        {
            get => _workingContent;
            set => SetField(ref _workingContent, value);
        }

        public string OriginalContentHash
        {
            get => _originalContentHash;
            set => SetField(ref _originalContentHash, value);
        }

        public RemoteFileStat? OriginalStat
        {
            get => _originalStat;
            set => SetField(ref _originalStat, value);
        }

        public DateTime LoadedAtUtc
        {
            get => _loadedAtUtc;
            set => SetField(ref _loadedAtUtc, value);
        }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (SetField(ref _isDirty, value))
                {
                    OnPropertyChanged(nameof(TabLabel));
                }
            }
        }

        public string TabLabel => IsDirty ? $"{FileName} *" : FileName;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
