using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    public static class TelegramChatCleaner
    {
        public static int ClearLocal(string projectPath)
        {
            var cleared = TelegramChatStore.Instance.ClearLocalChat(projectPath);
            TelegramPoller.Instance.RaiseThreadCleared(projectPath);
            return cleared;
        }

        public static async Task<(int Deleted, int LocalCleared)> WipeTelegramAndLocalAsync(
            string token,
            long chatId,
            string projectPath,
            TelegramBotClient client,
            CancellationToken cancellationToken)
        {
            var ids = TelegramChatStore.Instance.CollectDeletableTelegramMessageIds(projectPath).ToList();
            var deleted = 0;

            if (!string.IsNullOrWhiteSpace(token) && chatId != 0)
            {
                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (await client.DeleteMessageAsync(token, chatId, id, cancellationToken).ConfigureAwait(false))
                        {
                            deleted++;
                        }
                    }
                    catch
                    {
                        // Best-effort: old messages / already gone.
                    }

                    await Task.Delay(35, cancellationToken).ConfigureAwait(false);
                }
            }

            var localCleared = TelegramChatStore.Instance.ClearLocalChat(projectPath);
            TelegramPoller.Instance.RaiseThreadCleared(projectPath);
            return (deleted, localCleared);
        }

        public static string LocalSummary(int cleared)
            => Loc.T("telegram.clearLocalDone", cleared);

        public static string WipeSummary(int deleted, int localCleared)
            => Loc.T("telegram.wipeTelegramDone", deleted, localCleared);
    }
}
