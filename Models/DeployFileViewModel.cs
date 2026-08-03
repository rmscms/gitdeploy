using System.Windows.Media;
using GitDeployPro.Services;
using GitDeployPro.Services.Theme;
using MediaColor = System.Windows.Media.Color;
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

        public DeployFileViewModel(FileChange change)
        {
            Name = change.Name;
            Type = change.Type;
            DiffText = change.DiffPatch ?? string.Empty;
        }
    }
}
