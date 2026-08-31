using System;
using System.Windows;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using GitDeployPro.Windows;

namespace GitDeployPro.Services.Remote
{
    public static class TransferWorkerSettings
    {
        private static readonly ConfigurationService Config = new();

        public static int GetDefaultWorkers()
        {
            var value = Config.LoadGlobalConfig().DeployDefaultWorkers;
            if (value <= 0)
            {
                return TransferWorkerPrompt.DefaultWorkers;
            }

            return Math.Clamp(value, TransferWorkerPrompt.MinWorkers, TransferWorkerPrompt.MaxWorkers);
        }

        public static void SetDefaultWorkers(int workers)
        {
            var clamped = Math.Clamp(workers, TransferWorkerPrompt.MinWorkers, TransferWorkerPrompt.MaxWorkers);
            Config.UpdateGlobalConfig(cfg => cfg.DeployDefaultWorkers = clamped);
        }

        public static int ResolveForDeploy(int fileCount)
        {
            if (fileCount <= 1)
            {
                return 1;
            }

            return Math.Clamp(
                GetDefaultWorkers(),
                TransferWorkerPrompt.MinWorkers,
                Math.Min(TransferWorkerPrompt.MaxWorkers, fileCount));
        }

        public static bool TryConfigureDefault(DependencyObject? owner)
        {
            var current = GetDefaultWorkers();
            var dialog = new InputDialog(
                Loc.T("deploy.defaultWorkers.prompt"),
                Loc.T("deploy.defaultWorkers.title"),
                current.ToString());
            if (WindowOwnerService.ShowDialogOwned(dialog, owner) != true)
            {
                return false;
            }

            if (!int.TryParse((dialog.ResponseText ?? string.Empty).Trim(), out var workers))
            {
                workers = current;
            }

            SetDefaultWorkers(workers);
            return true;
        }
    }
}
