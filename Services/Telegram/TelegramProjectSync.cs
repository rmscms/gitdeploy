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
                    // SetCurrentProject tears down Deploy and clears ContentFrame —
                    // remount Deploy so Telegram switches don't leave a black empty shell.
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
