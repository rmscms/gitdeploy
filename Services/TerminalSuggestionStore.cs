using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class TerminalSuggestionStore
    {
        private static readonly ConfigurationService ConfigService = new();

        public static event Action? SuggestionsChanged;

        public static bool LoadAutocompleteEnabled()
        {
            return ConfigService.LoadGlobalConfig().TerminalAutocompleteEnabled;
        }

        public static void SaveAutocompleteEnabled(bool enabled)
        {
            ConfigService.UpdateGlobalConfig(cfg => cfg.TerminalAutocompleteEnabled = enabled);
            SuggestionsChanged?.Invoke();
        }

        public static bool LoadDictionaryModeEnabled()
        {
            return ConfigService.LoadGlobalConfig().TerminalDictionaryModeEnabled;
        }

        public static void SaveDictionaryModeEnabled(bool enabled)
        {
            ConfigService.UpdateGlobalConfig(cfg => cfg.TerminalDictionaryModeEnabled = enabled);
            SuggestionsChanged?.Invoke();
        }

        public static List<TerminalSuggestion> LoadAll()
        {
            var config = ConfigService.LoadGlobalConfig();
            var list = config.TerminalSuggestions ?? new List<TerminalSuggestion>();
            if (list.Count == 0)
            {
                list = TerminalSuggestionCatalog.GetAllBuiltInDefaults()
                    .Select(Clone)
                    .ToList();
                SaveAll(list, config.TerminalAutocompleteEnabled);
            }
            else
            {
                EnsureBuiltInPacks(list);
            }

            return list.Select(Clone).ToList();
        }

        public static void SaveAll(IEnumerable<TerminalSuggestion> suggestions, bool? autocompleteEnabled = null)
        {
            var snapshot = suggestions?.Select(Clone).ToList() ?? new List<TerminalSuggestion>();
            ConfigService.UpdateGlobalConfig(cfg =>
            {
                cfg.TerminalSuggestions = snapshot;
                if (autocompleteEnabled.HasValue)
                {
                    cfg.TerminalAutocompleteEnabled = autocompleteEnabled.Value;
                }
            });
            SuggestionsChanged?.Invoke();
        }

        public static void PromoteToGlobal(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var list = LoadAll();
            var item = list.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item == null || item.Scope == TerminalSuggestionScope.Global)
            {
                return;
            }

            item.Scope = TerminalSuggestionScope.Global;
            item.ProjectPath = string.Empty;
            SaveAll(list);
        }

        public static int MergeLaravelDefaults(bool overwriteExisting = false)
            => MergeBuiltInDefaults(overwriteExisting);

        public static int MergeBuiltInDefaults(bool overwriteExisting = false)
        {
            var list = LoadAll();
            var added = MergePack(list, TerminalSuggestionCatalog.GetAllBuiltInDefaults(), overwriteExisting);
            if (added > 0 || overwriteExisting)
            {
                SaveAll(list);
            }

            return added;
        }

        private static void EnsureBuiltInPacks(List<TerminalSuggestion> list)
        {
            var added = MergePack(list, TerminalSuggestionCatalog.GetAllBuiltInDefaults(), overwriteExisting: false);
            if (added > 0)
            {
                SaveAll(list);
            }
        }

        private static int MergePack(
            List<TerminalSuggestion> list,
            IEnumerable<TerminalSuggestion> defaults,
            bool overwriteExisting)
        {
            var added = 0;
            foreach (var def in defaults)
            {
                var existing = list.FirstOrDefault(s =>
                    string.Equals(s.Command, def.Command, StringComparison.OrdinalIgnoreCase)
                    && s.Scope == TerminalSuggestionScope.Global);

                if (existing == null)
                {
                    list.Add(Clone(def));
                    added++;
                    continue;
                }

                if (overwriteExisting)
                {
                    existing.Description = def.Description;
                    existing.Category = def.Category;
                    existing.IsEnabled = def.IsEnabled;
                }
            }

            return added;
        }

        public static string GetScopeLabel(TerminalSuggestion suggestion, string? currentProjectPath)
        {
            if (suggestion.Scope == TerminalSuggestionScope.Global)
            {
                return "global";
            }

            var projectPath = NormalizePath(suggestion.ProjectPath);
            var current = NormalizePath(currentProjectPath);
            if (!string.IsNullOrEmpty(projectPath)
                && !string.IsNullOrEmpty(current)
                && string.Equals(projectPath, current, StringComparison.OrdinalIgnoreCase))
            {
                return "This project";
            }

            if (!string.IsNullOrWhiteSpace(suggestion.ProjectPath))
            {
                try
                {
                    return Path.GetFileName(suggestion.ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch
                {
                    return suggestion.ProjectPath;
                }
            }

            return "project";
        }

        public static List<TerminalSuggestionTerminalItem> ResolveForTerminal(string? currentProjectPath)
        {
            var current = NormalizePath(currentProjectPath);
            return LoadAll()
                .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Command))
                .Select(s =>
                {
                    var scopeLabel = GetScopeLabel(s, current);
                    var isCurrent = s.Scope == TerminalSuggestionScope.Project
                        && !string.IsNullOrEmpty(current)
                        && string.Equals(NormalizePath(s.ProjectPath), current, StringComparison.OrdinalIgnoreCase);
                    var rank = isCurrent ? 0 : s.Scope == TerminalSuggestionScope.Global ? 1 : 2;
                    return new TerminalSuggestionTerminalItem
                    {
                        Command = s.Command,
                        Description = s.Description ?? string.Empty,
                        Scope = s.Scope == TerminalSuggestionScope.Global ? "global" : "project",
                        ScopeLabel = scopeLabel,
                        IsCurrentProject = isCurrent,
                        Rank = rank
                    };
                })
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Command, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<TerminalSuggestionRowViewModel> BuildSettingsRows(string? currentProjectPath)
        {
            var current = NormalizePath(currentProjectPath);
            return LoadAll()
                .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Command, StringComparer.OrdinalIgnoreCase)
                .Select(s =>
                {
                    var label = GetScopeLabel(s, current);
                    var isGlobal = s.Scope == TerminalSuggestionScope.Global;
                    var isCurrent = !isGlobal
                        && !string.IsNullOrEmpty(current)
                        && string.Equals(NormalizePath(s.ProjectPath), current, StringComparison.OrdinalIgnoreCase);
                    return new TerminalSuggestionRowViewModel
                    {
                        Suggestion = Clone(s),
                        ScopeLabel = label,
                        IsCurrentProjectScope = isCurrent,
                        IsGlobalScope = isGlobal,
                        IsOtherProjectScope = !isGlobal && !isCurrent
                    };
                })
                .ToList();
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        private static TerminalSuggestion Clone(TerminalSuggestion source)
        {
            return new TerminalSuggestion
            {
                Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString() : source.Id,
                Command = source.Command ?? string.Empty,
                Description = source.Description ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(source.Category)
                    ? TerminalSuggestionCatalog.CategoryCustom
                    : source.Category,
                IsEnabled = source.IsEnabled,
                Scope = source.Scope,
                ProjectPath = source.ProjectPath ?? string.Empty
            };
        }
    }
}
