using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDeployPro.Models;
using GitDeployPro.Services.Telegram;

namespace GitDeployPro.Services
{
    public static class TelegramTerminalCommandStore
    {
        public const int MaxCommandsPerProject = 12;

        public static List<TelegramTerminalCommand> LoadAll()
        {
            var config = new ConfigurationService().LoadGlobalConfig();
            var list = config.TelegramTerminalCommands ?? new List<TelegramTerminalCommand>();
            if (EnsureBuiltInPresets(list, config.RecentProjects))
            {
                new ConfigurationService().UpdateGlobalConfig(cfg => cfg.TelegramTerminalCommands = list);
            }

            return list.Select(Clone).ToList();
        }

        /// <summary>Seed known project presets once (e.g. crypto Go build).</summary>
        private static bool EnsureBuiltInPresets(
            List<TelegramTerminalCommand> list,
            List<ConfigurationService.RecentProjectEntry>? recentProjects)
        {
            var changed = false;
            foreach (var entry in recentProjects ?? new List<ConfigurationService.RecentProjectEntry>())
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                string folder;
                try
                {
                    folder = Path.GetFileName(entry.Path.Trim().TrimEnd('\\', '/'));
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(folder, "crypto", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectPath = NormalizePath(entry.Path);
                if (list.Any(c =>
                        PathsMatch(c.ProjectPath, projectPath)
                        && string.Equals(c.Description, "Go build", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                list.Add(TelegramTerminalCommandCatalog.CreateGoBuildExcore(projectPath));
                changed = true;
            }

            return changed;
        }

        public static void SaveAll(IEnumerable<TelegramTerminalCommand> commands)
        {
            var snapshot = commands?.Select(Clone).ToList() ?? new List<TelegramTerminalCommand>();
            new ConfigurationService().UpdateGlobalConfig(cfg => cfg.TelegramTerminalCommands = snapshot);
        }

        public static TelegramTerminalCommand? FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return LoadAll().FirstOrDefault(c =>
                string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static List<TelegramTerminalCommand> ResolveForProject(string? projectPath)
        {
            var current = NormalizePath(projectPath);
            if (string.IsNullOrEmpty(current))
            {
                return new List<TelegramTerminalCommand>();
            }

            return LoadAll()
                .Where(c => !string.IsNullOrWhiteSpace(c.Command)
                            && string.Equals(NormalizePath(c.ProjectPath), current, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCommandsPerProject)
                .ToList();
        }

        public static List<TelegramTerminalCommand> ListForSettings(string? currentProjectPath)
        {
            var current = NormalizePath(currentProjectPath);
            return LoadAll()
                .OrderBy(c => c.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var clone = Clone(c);
                    clone.ProjectPath = NormalizePath(clone.ProjectPath);
                    return clone;
                })
                .Where(c =>
                    string.IsNullOrEmpty(current)
                    || string.Equals(NormalizePath(c.ProjectPath), current, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static string NormalizePath(string? path)
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

        public static bool PathsMatch(string? a, string? b)
        {
            var left = NormalizePath(a);
            var right = NormalizePath(b);
            return !string.IsNullOrEmpty(left)
                   && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static TelegramTerminalCommand Clone(TelegramTerminalCommand source)
        {
            return new TelegramTerminalCommand
            {
                Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString() : source.Id,
                Description = source.Description ?? string.Empty,
                Command = source.Command ?? string.Empty,
                ProjectPath = NormalizePath(source.ProjectPath),
                NotifyBeforeRun = source.NotifyBeforeRun,
                NotifyMessage = source.NotifyMessage ?? string.Empty
            };
        }
    }
}
