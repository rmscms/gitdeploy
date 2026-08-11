using System;

namespace GitDeployPro.Models
{
    public enum TerminalSuggestionScope
    {
        Global,
        Project
    }

    public class TerminalSuggestion
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "Custom";
        public bool IsEnabled { get; set; } = true;
        public TerminalSuggestionScope Scope { get; set; } = TerminalSuggestionScope.Global;
        public string ProjectPath { get; set; } = string.Empty;
    }

    public class TerminalSuggestionTerminalItem
    {
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Scope { get; set; } = "global";
        public string ScopeLabel { get; set; } = "global";
        public bool IsCurrentProject { get; set; }
        public int Rank { get; set; }
    }

    public class TerminalSuggestionRowViewModel
    {
        public TerminalSuggestion Suggestion { get; init; } = new();
        public string ScopeLabel { get; set; } = "global";
        public bool IsCurrentProjectScope { get; set; }
        public bool IsGlobalScope { get; set; }
        public bool IsOtherProjectScope { get; set; }

        public string Command => Suggestion.Command;
        public string Description => Suggestion.Description;
        public string Category => Suggestion.Category;
        public bool IsEnabled
        {
            get => Suggestion.IsEnabled;
            set => Suggestion.IsEnabled = value;
        }

        public bool CanPromoteToGlobal =>
            Suggestion.Scope == TerminalSuggestionScope.Project;
    }

}
