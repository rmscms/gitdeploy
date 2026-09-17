using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;
using WpfApplication = System.Windows.Application;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Builds a Telegram-friendly preview of files that would go out on Deploy (branch compare + local).
    /// </summary>
    public static class TelegramDeployPreview
    {
        private static readonly string[] SensitiveDeleteSuffixes =
        {
            ".db", ".sqlite", ".sqlite3", ".env", ".pem", ".key", ".pfx", ".p12"
        };

        public static async Task<string> BuildHtmlAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return TelegramMarkup.Html(Loc.T("telegram.deployNoProject"));
            }

            if (!TelegramProjectProfile.SupportsDeploy(projectPath))
            {
                return TelegramMarkup.Html(Loc.T(
                    "telegram.deployNotAvailable",
                    TelegramProjectProfile.GetKindLabel(projectPath)));
            }

            try
            {
                return await InvokeOnUiAsync(async () =>
                {
                    GitService.SetWorkingDirectory(projectPath);
                    var git = new GitService();
                    var config = new ConfigurationService().LoadProjectConfig(projectPath);
                    var source = string.IsNullOrWhiteSpace(config.DefaultSourceBranch)
                        ? "master"
                        : config.DefaultSourceBranch.Trim();
                    var target = (config.DefaultTargetBranch ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(target) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return TelegramMarkup.Html(Loc.T("telegram.previewBadBranches", source, target));
                    }

                    var changes = await git.GetDiffAsync(source, target).ConfigureAwait(true);
                    var currentBranch = await git.GetCurrentBranchAsync().ConfigureAwait(true);
                    if (string.Equals(source, currentBranch, StringComparison.OrdinalIgnoreCase))
                    {
                        var local = await git.GetUncommittedChangesAsync(includeDiff: false).ConfigureAwait(true);
                        var existing = new HashSet<string>(changes.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                        foreach (var item in local)
                        {
                            if (existing.Add(item.Name))
                            {
                                changes.Add(item);
                            }
                        }
                    }

                    if (changes.Count == 0)
                    {
                        return TelegramMarkup.Html(Loc.T("telegram.previewEmpty"));
                    }

                    var lines = new List<string>();
                    var sensitiveDeletes = new List<string>();
                    foreach (var change in changes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        lines.Add(FormatLine(change));
                        if (change.Type == ChangeType.Deleted && IsSensitivePath(change.Name))
                        {
                            sensitiveDeletes.Add(change.Name);
                        }
                    }

                    var sb = new StringBuilder();
                    sb.Append(TelegramMarkup.Html(Loc.T("telegram.previewIntro", source, target)));
                    sb.Append("\n\n<pre>");
                    sb.Append(TelegramMarkup.Html(string.Join("\n", lines)));
                    sb.Append("</pre>");

                    if (sensitiveDeletes.Count > 0)
                    {
                        sb.Append("\n\n");
                        sb.Append(TelegramMarkup.Html(Loc.T(
                            "telegram.previewSensitiveDelete",
                            string.Join(", ", sensitiveDeletes.Select(p => "D " + p)))));
                    }

                    return sb.ToString();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return TelegramMarkup.Html(Loc.T("telegram.previewFailed", ex.Message));
            }
        }

        private static string FormatLine(FileChange change)
        {
            if (change.IsRename && !string.IsNullOrWhiteSpace(change.OldPath))
            {
                return $"R {change.OldPath} -> {change.Name}";
            }

            var code = change.Type switch
            {
                ChangeType.Added => "A",
                ChangeType.Deleted => "D",
                _ => "M"
            };
            return $"{code} {change.Name}";
        }

        private static bool IsSensitivePath(string path)
        {
            var name = Path.GetFileName(path ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            foreach (var suffix in SensitiveDeleteSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return string.Equals(name, ".env", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);
        }

        private static Task<T> InvokeOnUiAsync<T>(Func<Task<T>> work)
        {
            var app = WpfApplication.Current;
            if (app?.Dispatcher == null)
            {
                return work();
            }

            if (app.Dispatcher.CheckAccess())
            {
                return work();
            }

            return app.Dispatcher.InvokeAsync(work).Task.Unwrap();
        }
    }
}
