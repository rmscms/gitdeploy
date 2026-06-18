using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace GitDeployPro.Services
{
    public class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "GitDeployPro";
        public string PrimaryValueName => ValueName;

        public void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return;

                if (enable)
                {
                    var exePath = GetCurrentExecutablePath();
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        return;
                    }

                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }

        public bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key == null) return false;
                var value = key.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        public string GetCurrentExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public StartupEntryInfo? GetPrimaryStartupEntry()
        {
            return GetStartupEntryByName(ValueName);
        }

        public IReadOnlyList<StartupEntryInfo> GetGitDeployStartupEntries()
        {
            var results = new List<StartupEntryInfo>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key == null)
                {
                    return results;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    var command = key.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        continue;
                    }

                    var entry = CreateEntry(valueName, command);
                    if (IsGitDeployCandidate(entry))
                    {
                        results.Add(entry);
                    }
                }
            }
            catch
            {
            }

            return results
                .OrderBy(x => x.ValueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public int RemoveStartupEntries(IEnumerable<string> valueNames)
        {
            if (valueNames == null)
            {
                return 0;
            }

            var removed = 0;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null)
                {
                    return 0;
                }

                foreach (var valueName in valueNames
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (key.GetValue(valueName) == null)
                    {
                        continue;
                    }

                    key.DeleteValue(valueName, false);
                    removed++;
                }
            }
            catch
            {
            }

            return removed;
        }

        private StartupEntryInfo? GetStartupEntryByName(string valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName))
            {
                return null;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key == null)
                {
                    return null;
                }

                var command = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(command))
                {
                    return null;
                }

                return CreateEntry(valueName, command);
            }
            catch
            {
                return null;
            }
        }

        private static StartupEntryInfo CreateEntry(string valueName, string command)
        {
            return new StartupEntryInfo
            {
                ValueName = valueName ?? string.Empty,
                Command = command ?? string.Empty,
                ExecutablePath = ExtractExecutablePath(command)
            };
        }

        private static bool IsGitDeployCandidate(StartupEntryInfo entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(entry.ValueName) &&
                entry.ValueName.Contains("GitDeploy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(entry.Command) &&
                entry.Command.Contains("GitDeploy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var fileName = string.IsNullOrWhiteSpace(entry.ExecutablePath)
                ? string.Empty
                : Path.GetFileName(entry.ExecutablePath);

            return !string.IsNullOrWhiteSpace(fileName) &&
                   fileName.Contains("GitDeploy", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return string.Empty;
            }

            var trimmed = command.Trim();
            var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                var pathWithMaybeQuote = trimmed[..(exeIndex + 4)];
                return pathWithMaybeQuote.Trim().Trim('"');
            }

            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                var closingQuote = trimmed.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    return trimmed[1..closingQuote].Trim();
                }
            }

            var firstSpace = trimmed.IndexOf(' ');
            return (firstSpace > 0 ? trimmed[..firstSpace] : trimmed).Trim().Trim('"');
        }

        public sealed class StartupEntryInfo
        {
            public string ValueName { get; init; } = string.Empty;
            public string Command { get; init; } = string.Empty;
            public string ExecutablePath { get; init; } = string.Empty;
        }
    }
}

