using System.Windows.Media;
using GitDeployPro.Services;

namespace GitDeployPro.Models
{
    public class DeployFileViewModel
    {
        public string Name { get; set; }
        public ChangeType Type { get; set; }
        public bool IsSelected { get; set; } = true;

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
                switch (Type)
                {
                    case ChangeType.Added: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50));
                    case ChangeType.Modified: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 143, 0));
                    case ChangeType.Deleted: return new SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));
                    default: return new SolidColorBrush(System.Windows.Media.Colors.Gray);
                }
            }
        }

        public DeployFileViewModel(FileChange change)
        {
            Name = change.Name;
            Type = change.Type;
        }
    }
}
