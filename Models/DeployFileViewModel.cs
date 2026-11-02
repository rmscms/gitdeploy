using System.IO;
using System.Windows;
using System.Windows.Media;
using GitDeployPro.Services;
using GitDeployPro.Services.Theme;
using MediaColor = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;
using MediaColors = System.Windows.Media.Colors;

namespace GitDeployPro.Models
{
    public class DeployFileViewModel
    {
        public string Name { get; set; }
        public ChangeType Type { get; set; }
        public bool IsSelected { get; set; } = true;
        public string DiffText { get; }
        public bool HasDiff => !string.IsNullOrWhiteSpace(DiffText);

        public string StatusText
        {
            get
            {
                switch (Type)
                {
                    case ChangeType.Added: return "NEW";
                    case ChangeType.Modified: return "MODIFIED";
                    case ChangeType.Deleted: return "DELETED";
                    default: return "";
                }
            }
        }

        public SolidColorBrush StatusColor
        {
            get
            {
                var tokens = ThemeService.Instance.CurrentTokens;
                return Type switch
                {
                    ChangeType.Added => tokens.GetBrush(
                        "changedFiles.statusAdded",
                        MediaColor.FromRgb(46, 125, 50)),
                    ChangeType.Modified => tokens.GetBrush(
                        "changedFiles.statusModified",
                        MediaColor.FromRgb(255, 143, 0)),
                    ChangeType.Deleted => tokens.GetBrush(
                        "changedFiles.statusDeleted",
                        MediaColor.FromRgb(198, 40, 40)),
                    _ => tokens.GetBrush(
                        "changedFiles.statusDefault",
                        MediaColors.Gray)
                };
            }
        }

        // ---- row presentation (theme/_build/pages/20-deploy-git.cjs: .frow / .badge) ----
        public string DirectoryPart
        {
            get
            {
                var i = (Name ?? string.Empty).LastIndexOf('/');
                return i < 0 ? string.Empty : Name.Substring(0, i + 1);
            }
        }

        public string FileNamePart
        {
            get
            {
                var i = (Name ?? string.Empty).LastIndexOf('/');
                return i < 0 ? Name ?? string.Empty : Name.Substring(i + 1);
            }
        }

        public string IconKind
        {
            get
            {
                var ext = Path.GetExtension(Name ?? string.Empty).ToLowerInvariant();
                return ext switch
                {
                    ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" => "image",
                    ".php" or ".js" or ".ts" or ".css" or ".scss" or ".html" or ".json" or ".cs" or ".xaml" or ".cjs" or ".mjs" => "file-code",
                    _ => "file-text"
                };
            }
        }

        public Brush IconBrush
        {
            get
            {
                var ext = Path.GetExtension(Name ?? string.Empty).ToLowerInvariant();
                var hex = ext switch
                {
                    ".php" => "#8892BF",
                    ".js" or ".ts" => "#F0DB4F",
                    ".css" or ".scss" => "#5BB8FF",
                    ".html" => "#FF8A4C",
                    ".json" => "#5CFFB0",
                    ".md" => "#C3CEE3",
                    ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" => "#FF7AC6",
                    ".cs" => "#B48BFF",
                    ".xaml" => "#4C8DFF",
                    _ => null
                };
                return hex != null
                    ? (Brush)new BrushConverter().ConvertFromString(hex)!
                    : (Brush)(Application.Current?.TryFindResource("Text.Muted") ?? Brushes.Gray);
            }
        }

        private string StatusKey => Type switch
        {
            ChangeType.Added => "Success",
            ChangeType.Modified => "Warning",
            ChangeType.Deleted => "Error",
            _ => "Info"
        };

        public Brush BadgeForeground => (Brush)Application.Current.FindResource("Status." + StatusKey);

        public Brush BadgeBackground => (Brush)Application.Current.FindResource("Status." + StatusKey + "Surface");

        public Brush BadgeBorder
        {
            get
            {
                var c = ((SolidColorBrush)BadgeForeground).Color;
                var b = new SolidColorBrush(MediaColor.FromArgb(0x40, c.R, c.G, c.B));
                b.Freeze();
                return b;
            }
        }

        public bool IsDeleted => Type == ChangeType.Deleted;

        public DeployFileViewModel(FileChange change)
        {
            Name = change.Name;
            Type = change.Type;
            DiffText = change.DiffPatch ?? string.Empty;
        }
    }
}
