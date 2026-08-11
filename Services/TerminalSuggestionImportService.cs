using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public sealed class TerminalSuggestionImportResult
    {
        public int Added { get; init; }
        public int SkippedDuplicates { get; init; }
        public int InvalidRows { get; init; }
        public List<string> Errors { get; init; } = new();
        public string? ResolvedCategory { get; init; }
    }

    public static class TerminalSuggestionImportService
    {
        public static string GetSampleFilePath()
        {
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Resources", "TerminalSuggestionsImport.sample.json");
        }

        public static TerminalSuggestionImportResult ImportFromFile(
            string filePath,
            string? category,
            TerminalSuggestionScope scope,
            string? projectPath,
            bool skipDuplicates)
        {
            var result = new TerminalSuggestionImportResult();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                result.Errors.Add("JSON file not found.");
                return result;
            }

            List<ImportCommandEntry> entries;
            string? fileCategory = null;

            try
            {
                using var stream = File.OpenRead(filePath);
                using var document = JsonDocument.Parse(stream);
                entries = ParseEntries(document.RootElement, out fileCategory);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Invalid JSON: {ex.Message}");
                return result;
            }

            var resolvedCategory = !string.IsNullOrWhiteSpace(category)
                ? category.Trim()
                : !string.IsNullOrWhiteSpace(fileCategory)
                    ? fileCategory.Trim()
                    : TerminalSuggestionCatalog.CategoryCustom;

            if (string.IsNullOrWhiteSpace(resolvedCategory))
            {
                result.Errors.Add("Category name is required (enter in UI or include in JSON).");
                return result;
            }

            var normalizedProjectPath = scope == TerminalSuggestionScope.Project
                ? (projectPath ?? string.Empty).Trim()
                : string.Empty;

            if (scope == TerminalSuggestionScope.Project && string.IsNullOrWhiteSpace(normalizedProjectPath))
            {
                result.Errors.Add("No project is open — choose Global scope or open a project first.");
                return result;
            }

            var existing = TerminalSuggestionStore.LoadAll();
            var existingKeys = new HashSet<string>(
                existing.Select(s => BuildDedupeKey(s.Command, s.Scope, s.ProjectPath)),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            var skipped = 0;
            var invalid = 0;

            foreach (var entry in entries)
            {
                var command = entry.Command?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(command))
                {
                    invalid++;
                    continue;
                }

                var key = BuildDedupeKey(command, scope, normalizedProjectPath);
                if (skipDuplicates && existingKeys.Contains(key))
                {
                    skipped++;
                    continue;
                }

                var item = new TerminalSuggestion
                {
                    Command = command,
                    Description = entry.Description?.Trim() ?? string.Empty,
                    Category = resolvedCategory,
                    IsEnabled = entry.Enabled ?? true,
                    Scope = scope,
                    ProjectPath = normalizedProjectPath
                };

                existing.Add(item);
                existingKeys.Add(key);
                added++;
            }

            if (added > 0)
            {
                TerminalSuggestionStore.SaveAll(existing);
            }

            return new TerminalSuggestionImportResult
            {
                Added = added,
                SkippedDuplicates = skipped,
                InvalidRows = invalid,
                ResolvedCategory = resolvedCategory,
                Errors = result.Errors
            };
        }

        private static string BuildDedupeKey(string command, TerminalSuggestionScope scope, string projectPath)
        {
            var scopeKey = scope == TerminalSuggestionScope.Global ? "global" : projectPath.Trim();
            return $"{command}|{scopeKey}";
        }

        private static List<ImportCommandEntry> ParseEntries(JsonElement root, out string? category)
        {
            category = null;
            var list = new List<ImportCommandEntry>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    list.Add(ParseCommandEntry(item));
                }

                return list;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Root must be an object or array.");
            }

            if (root.TryGetProperty("category", out var categoryProp) && categoryProp.ValueKind == JsonValueKind.String)
            {
                category = categoryProp.GetString();
            }

            if (!root.TryGetProperty("commands", out var commandsProp))
            {
                throw new InvalidDataException("Missing \"commands\" array.");
            }

            if (commandsProp.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("\"commands\" must be an array.");
            }

            foreach (var item in commandsProp.EnumerateArray())
            {
                list.Add(ParseCommandEntry(item));
            }

            return list;
        }

        private static ImportCommandEntry ParseCommandEntry(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return new ImportCommandEntry();
            }

            string? command = null;
            if (item.TryGetProperty("command", out var cmdProp) && cmdProp.ValueKind == JsonValueKind.String)
            {
                command = cmdProp.GetString();
            }

            string? description = null;
            if (item.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                description = descProp.GetString();
            }

            bool? enabled = null;
            if (item.TryGetProperty("enabled", out var enabledProp))
            {
                if (enabledProp.ValueKind == JsonValueKind.True)
                {
                    enabled = true;
                }
                else if (enabledProp.ValueKind == JsonValueKind.False)
                {
                    enabled = false;
                }
            }

            return new ImportCommandEntry
            {
                Command = command,
                Description = description,
                Enabled = enabled
            };
        }

        private sealed class ImportCommandEntry
        {
            public string? Command { get; init; }
            public string? Description { get; init; }
            public bool? Enabled { get; init; }
        }
    }
}
