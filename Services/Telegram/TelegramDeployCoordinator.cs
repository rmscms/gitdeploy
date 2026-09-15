using System;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;
using WpfApplication = System.Windows.Application;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Telegram deploy UX: Deploy is always on the keyboard; run GitDeploy pipeline on demand.
    /// </summary>
    public static class TelegramDeployCoordinator
    {
        private static int _deployBusy;

        public static async Task<int> GetPendingChangeCountAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return 0;
            }

            try
            {
                return await InvokeOnUiAsync(async () =>
                {
                    GitService.SetWorkingDirectory(projectPath);
                    return await new GitService().GetUncommittedCountAsync().ConfigureAwait(true);
                }).ConfigureAwait(false);
            }
            catch
            {
                return 0;
            }
        }

        public static Task<JObject> BuildReplyKeyboardAsync(string projectPath)
        {
            // Always expose Deploy so Telegram clients do not keep a stale 3-button keyboard.
            return Task.FromResult(BuildReplyKeyboard());
        }

        public static JObject BuildReplyKeyboard(bool showDeploy = true)
        {
            // Compact rows: agent controls on a third short row.
            return TelegramMarkup.ReplyKeyboard(
                new[]
                {
                    Loc.T("telegram.kbProjects"),
                    Loc.T("telegram.kbStatus"),
                    Loc.T("telegram.kbDeploy")
                },
                new[]
                {
                    Loc.T("telegram.kbClear"),
                    Loc.T("telegram.kbWipeTelegram"),
                    Loc.T("telegram.kbHelp")
                },
                new[]
                {
                    Loc.T("telegram.kbRestartAgent"),
                    Loc.T("telegram.kbModel")
                });
        }

        /// <summary>
        /// Telegram often keeps an old reply keyboard until remove+replace.
        /// </summary>
        public static async Task ForceResetKeyboardAsync(
            string token,
            long chatId,
            string projectPath,
            TelegramBotClient client,
            CancellationToken cancellationToken,
            string? hintText = null)
        {
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
            {
                return;
            }

            var pending = await GetPendingChangeCountAsync(projectPath).ConfigureAwait(false);
            var text = hintText
                       ?? (pending > 0
                           ? Loc.T("telegram.deployReadyHint", pending)
                           : Loc.T("telegram.keyboardReset"));

            try
            {
                var removeId = await client.SendMessageAsync(
                    token,
                    chatId,
                    Loc.T("telegram.keyboardResetting"),
                    cancellationToken,
                    TelegramMarkup.RemoveReplyKeyboard()).ConfigureAwait(false);
                TelegramChatStore.Instance.TrackBotMessageId(projectPath, removeId);

                var readyId = await client.SendMessageAsync(
                    token,
                    chatId,
                    text,
                    cancellationToken,
                    BuildReplyKeyboard()).ConfigureAwait(false);
                TelegramChatStore.Instance.TrackBotMessageId(projectPath, readyId);
            }
            catch
            {
                // Best-effort keyboard repair.
            }
        }

        public static async Task RefreshKeyboardHintAsync(
            string token,
            long chatId,
            string projectPath,
            TelegramBotClient client,
            CancellationToken cancellationToken)
        {
            await ForceResetKeyboardAsync(token, chatId, projectPath, client, cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task<TelegramDeployResult> RunAsync(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return TelegramDeployResult.Fail(Loc.T("telegram.deployNoProject"));
            }

            if (Interlocked.CompareExchange(ref _deployBusy, 1, 0) != 0)
            {
                return TelegramDeployResult.Fail(Loc.T("telegram.deployBusy"));
            }

            try
            {
                return await InvokeOnUiAsync(async () =>
                {
                    var app = WpfApplication.Current;
                    if (app?.MainWindow is not MainWindow main)
                    {
                        return TelegramDeployResult.Fail(Loc.T("telegram.deployAppNotReady"));
                    }

                    return await main.RunTelegramDeployAsync(projectPath).ConfigureAwait(true);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return TelegramDeployResult.Fail(Loc.T("telegram.deployFailed", ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _deployBusy, 0);
            }
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
