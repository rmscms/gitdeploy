using System;
using System.Windows;
using GitDeployPro.Windows;

namespace GitDeployPro.Services.Remote
{
    public static class TransferWorkerPrompt
    {
        public const int MinWorkers = 1;
        public const int MaxWorkers = 8;
        public const int DefaultWorkers = 8;

        public static int GetDefaultWorkers() => TransferWorkerSettings.GetDefaultWorkers();

        public static bool TryAsk(
            DependencyObject context,
            string action,
            int fileCount,
            out int workers,
            bool forceAsk = false)
        {
            workers = 1;
            if (!forceAsk && fileCount <= 1)
            {
                return true;
            }

            var suggested = forceAsk
                ? GetDefaultWorkers()
                : Math.Clamp(Math.Min(GetDefaultWorkers(), Math.Max(fileCount, 2)), MinWorkers, MaxWorkers);
            var countLine = fileCount > 1 && !forceAsk
                ? $"{fileCount} files ready to {action}."
                : $"Folder ready to {action}.";
            var dialog = new InputDialog(
                $"{countLine}\n\n" +
                "How many simultaneous workers? (1–8)\n" +
                "Each worker takes whole files (not chunks). Then we verify everything arrived.",
                "How many workers?",
                suggested.ToString());

            if (WindowOwnerService.ShowDialogOwned(dialog, context) != true)
            {
                return false;
            }

            if (!int.TryParse((dialog.ResponseText ?? string.Empty).Trim(), out workers))
            {
                workers = suggested;
            }

            var maxAllowed = forceAsk ? MaxWorkers : Math.Min(MaxWorkers, Math.Max(fileCount, 1));
            workers = Math.Clamp(workers, MinWorkers, maxAllowed);
            return true;
        }
    }
}
