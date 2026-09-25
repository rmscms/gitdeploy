using System;
using GitDeployPro.Services;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Mirrors Telegram active-project switches onto the GitDeploy workspace UI.
    /// </summary>
    public static class TelegramProjectSync
    {
        public static void ApplyToGitDeploy(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                TelegramChatStore.Instance.SetActiveProjectPath(
                    string.IsNullOrWhiteSpace(projectPath) ? TelegramPaths.UnassignedPath : projectPath);
                return;
            }

            var path = projectPath.Trim();
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null)
            {
                TelegramChatStore.Instance.SetActiveProjectPath(path);
                ProjectWorkspace.Notify(path);
                AgentFacade.PrewarmForProject(path);
                return;
            }

            void Apply()
            {
                if (app.MainWindow is MainWindow main)
                {
                    // Same project: do not rebuild Deploy. That disposed the open SSH terminal.
                    if (main.HasLiveDeploySession(path))
                    {
                        TelegramChatStore.Instance.SetActiveProjectPath(path);
                        return;
                    }

                    // A real project change tears down Deploy — remount so the frame is not left empty.
                    main.SetCurrentProject(path, showSetupWizard: false);
                    main.NavigateToDeploy();
                    return;
                }

                TelegramChatStore.Instance.SetActiveProjectPath(path);
                ProjectWorkspace.Notify(path);
                AgentFacade.PrewarmForProject(path);
            }

            if (app.Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                app.Dispatcher.Invoke(Apply);
            }
        }
    }
}
