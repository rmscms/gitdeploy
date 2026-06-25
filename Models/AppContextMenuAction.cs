using System;
using System.Windows.Input;

namespace GitDeployPro.Models
{
    public sealed class AppContextMenuAction
    {
        public string Id { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string? IconGlyph { get; init; }
        public bool IsEnabled { get; init; } = true;
        public bool IsVisible { get; init; } = true;
        public bool IsSeparator { get; init; }
        public bool IsDestructive { get; init; }
        public ICommand? Command { get; init; }
        public object? CommandParameter { get; init; }
        public Action<object?>? Execute { get; init; }

        public static AppContextMenuAction Separator(string id = "separator")
        {
            return new AppContextMenuAction
            {
                Id = id,
                IsSeparator = true
            };
        }
    }
}
