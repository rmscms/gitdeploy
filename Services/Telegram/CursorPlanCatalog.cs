using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>Lists plan markdown files under {project}/.cursor/plans/ (newest first).</summary>
    internal static class CursorPlanCatalog
    {
        public static string PlansDirectory(string projectPath)
            => Path.Combine(projectPath ?? string.Empty, ".cursor", "plans");

        public static IReadOnlyList<string> List(string projectPath, int max = 30)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                return Array.Empty<string>();
            }

            var dir = PlansDirectory(projectPath);
            if (!Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .Where(p => !string.Equals(Path.GetFileName(p), ".gitignore", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(max, 1, 50))
                .ToList();
        }

        /// <summary>Newest plan on disk by LastWriteTimeUtc.</summary>
        public static string? GetLatest(string projectPath)
            => List(projectPath, max: 1).FirstOrDefault();

        public static string? GetByIndex(string projectPath, int index)
        {
            if (index < 0)
            {
                return null;
            }

            var plans = List(projectPath);
            return index < plans.Count ? plans[index] : null;
        }

        public static void Remember(string projectPath, string? planPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)
                || string.IsNullOrWhiteSpace(planPath)
                || !File.Exists(planPath))
            {
                return;
            }

            TelegramChatStore.Instance.RegisterPlanCallback(projectPath, planPath);
        }

        public static bool TryDelete(string projectPath, string planPath, out string? error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
                {
                    error = "missing";
                    return false;
                }

                var full = Path.GetFullPath(planPath);
                var plansDir = Path.GetFullPath(PlansDirectory(projectPath));
                if (!full.StartsWith(
                        plansDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "outside";
                    return false;
                }

                File.Delete(full);
                TelegramChatStore.Instance.ForgetPlanPath(projectPath, full);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static int DeleteAll(string projectPath)
        {
            var n = 0;
            foreach (var path in List(projectPath, max: 50).ToList())
            {
                if (TryDelete(projectPath, path, out _))
                {
                    n++;
                }
            }

            return n;
        }

        public static string? ResolveByFileName(string projectPath, string fileName)
        {
            var name = Path.GetFileName((fileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                return null;
            }

            var path = Path.Combine(PlansDirectory(projectPath), name);
            return File.Exists(path) ? path : null;
        }

        public static bool IsUnderPlansDir(string projectPath, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var full = Path.GetFullPath(path);
                var plansDir = Path.GetFullPath(PlansDirectory(projectPath));
                return full.StartsWith(
                    plansDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
