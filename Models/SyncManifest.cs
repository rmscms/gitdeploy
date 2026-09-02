using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitDeployPro.Models
{
    public sealed class SyncManifest
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public DateTime? UpdatedUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public List<SyncManifestPathEntry> Paths { get; set; } = new();
        public List<PathMapping> Mappings { get; set; } = new();
    }

    public sealed class SyncManifestPathEntry
    {
        public string Remote { get; set; } = string.Empty;
        public string Kind { get; set; } = "file";

        public bool IsDirectory =>
            string.Equals(Kind, "folder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Kind, "dir", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class SyncManifestLoadResult
    {
        public bool Found { get; init; }
        public SyncManifest Manifest { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed class SyncPathPreviewItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public string Remote { get; set; } = string.Empty;
        public string Kind { get; set; } = "file";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public string DisplayKind => IsDirectory ? "folder" : "file";

        public bool IsDirectory =>
            string.Equals(Kind, "folder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Kind, "dir", StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
