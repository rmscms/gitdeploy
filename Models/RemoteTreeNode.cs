using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GitDeployPro.Services;

namespace GitDeployPro.Models
{
    public sealed class RemoteTreeNode : INotifyPropertyChanged, ITreeMultiSelectable
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isMultiSelected;
        private bool _isLoaded;
        private string _iconColor = "#FF9AA8B5";
        private string _badgeText = "FILE";
        private string _badgeBackground = "#1A2B3442";
        private string _badgeBorder = "#2A3E4F69";
        private string _badgeForeground = "#FF9AA8B5";
        private bool _isSearchVisible = true;
        private string? _namePrefix;
        private string _nameMatch = string.Empty;
        private string _nameSuffix = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsPlaceholder { get; set; }
        public long SizeBytes { get; set; }
        public string SizeLabel { get; set; } = string.Empty;
        public string ModifiedLabel { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "📄";

        public string IconColor
        {
            get => _iconColor;
            set => SetProperty(ref _iconColor, value);
        }

        public string BadgeText
        {
            get => _badgeText;
            set => SetProperty(ref _badgeText, value);
        }

        public string BadgeBackground
        {
            get => _badgeBackground;
            set => SetProperty(ref _badgeBackground, value);
        }

        public string BadgeBorder
        {
            get => _badgeBorder;
            set => SetProperty(ref _badgeBorder, value);
        }

        public string BadgeForeground
        {
            get => _badgeForeground;
            set => SetProperty(ref _badgeForeground, value);
        }

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

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set => SetProperty(ref _isMultiSelected, value);
        }

        public bool IncludeInMultiSelect => !IsPlaceholder;

        IEnumerable ITreeMultiSelectable.Children => Children;

        public bool IsLoaded
        {
            get => _isLoaded;
            set => SetProperty(ref _isLoaded, value);
        }

        public bool IsSearchVisible
        {
            get => _isSearchVisible;
            set
            {
                if (SetProperty(ref _isSearchVisible, value, nameof(IsSearchVisible)))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchVisibility)));
                }
            }
        }

        public Visibility SearchVisibility =>
            _isSearchVisible ? Visibility.Visible : Visibility.Collapsed;

        public string NamePrefix => _namePrefix ?? Name;
        public string NameMatch => _nameMatch;
        public string NameSuffix => _nameSuffix;

        public void ApplySearchVisual(bool visible, TreeNameSearch.NameParts parts, bool expand)
        {
            IsSearchVisible = visible;
            _namePrefix = parts.Prefix;
            SetProperty(ref _nameMatch, parts.Match ?? string.Empty, nameof(NameMatch));
            SetProperty(ref _nameSuffix, parts.Suffix ?? string.Empty, nameof(NameSuffix));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamePrefix)));
            if (expand)
            {
                IsExpanded = true;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
