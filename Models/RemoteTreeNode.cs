using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitDeployPro.Models
{
    public sealed class RemoteTreeNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isLoaded;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsPlaceholder { get; set; }
        public long SizeBytes { get; set; }
        public string SizeLabel { get; set; } = string.Empty;
        public string ModifiedLabel { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "📄";
        public string IconColor { get; set; } = "#FF9AA8B5";
        public string BadgeText { get; set; } = "FILE";
        public string BadgeBackground { get; set; } = "#1A2B3442";
        public string BadgeBorder { get; set; } = "#2A3E4F69";
        public string BadgeForeground { get; set; } = "#FF9AA8B5";
        public ObservableCollection<RemoteTreeNode> Children { get; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsLoaded
        {
            get => _isLoaded;
            set => SetProperty(ref _isLoaded, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
