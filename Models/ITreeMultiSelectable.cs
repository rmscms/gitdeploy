using System.Collections;

namespace GitDeployPro.Models
{
    public interface ITreeMultiSelectable
    {
        bool IsMultiSelected { get; set; }
        bool IsExpanded { get; }
        bool IncludeInMultiSelect { get; }
        IEnumerable Children { get; }
    }
}
