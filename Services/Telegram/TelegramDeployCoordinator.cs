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
            if (string.IsNullOrWhiteSpace(projectPath)
                || TelegramPaths.IsUnassigned(projectPath)
                || !TelegramProjectProfile.SupportsDeploy(projectPath))
            {
                return 0;
            }

            try
            {
                return await InvokeOnUiAsync(
                    async () =>
                    {
                        GitService.SetWorkingDirectory(projectPath);
                        return await new GitService().GetUncommittedCountAsync().ConfigureAwait(true);
                    },
                    TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            }
            catch
            {
                return 0;
            }
        }

        public static Task<JObject> BuildReplyKeyboardAsync(string projectPath)
        {
            return Task.FromResult(BuildReplyKeyboard(projectPath));
        }

        public static JObject BuildReplyKeyboard(string? projectPath = null)
        {
            var showDeploy = TelegramProjectProfile.SupportsDeploy(projectPath);
            var planMode = !string.IsNullOrWhiteSpace(projectPath)
                           && TelegramChatStore.Instance.IsCursorPlanMode(projectPath);
            var modeButton = planMode
                ? Loc.T("telegram.kbAgent")
                : Loc.T("telegram.kbPlan");
            if (showDeploy)
            {
                return TelegramMarkup.ReplyKeyboard(
                    new[]
                    {
                        Loc.T("telegram.kbProjects"),
                        Loc.T("telegram.kbStatus"),
                        Loc.T("telegram.kbDeploy")
                    },
                    new[]
                    {
                        Loc.T("telegram.kbPreview"),
                        Loc.T("telegram.kbClear"),
                        Loc.T("telegram.kbEngine")
                    },
                    new[]
                    {
                        Loc.T("telegram.kbRestartAgent"),
                        Loc.T("telegram.kbModel"),
                        modeButton
                    });
            }

            return TelegramMarkup.ReplyKeyboard(
                new[]
                {
                    Loc.T("telegram.kbProjects"),
                    Loc.T("telegram.kbStatus"),
                    Loc.T("telegram.kbClear")
                },
                new[]
                {
                    Loc.T("telegram.kbEngine"),
                    Loc.T("telegram.kbRestartAgent"),
                    Loc.T("telegram.kbModel")
                },
                new[]
                {
                    modeButton
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
                    BuildReplyKeyboard(projectPath)).ConfigureAwait(false);
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

            if (!TelegramProjectProfile.SupportsDeploy(projectPath))
            {
                return TelegramDeployResult.Fail(
                    Loc.T("telegram.deployNotAvailable", TelegramProjectProfile.GetKindLabel(projectPath)));
            }

            if (Interlocked.CompareExchange(ref _deployBusy, 1, 0) != 0)
            {
                return TelegramDeployResult.Fail(Loc.T("telegram.deployBusy"));
            }

            try
            {
                return await InvokeOnUiAsync(
                    async () =>
                    {
                        var app = WpfApplication.Current;
                        if (app?.MainWindow is not MainWindow main)
                        {
                            return TelegramDeployResult.Fail(Loc.T("telegram.deployAppNotReady"));
                        }

                        return await main.RunTelegramDeployAsync(projectPath).ConfigureAwait(true);
                    },
                    TimeSpan.FromMinutes(30)).ConfigureAwait(false);
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

        private static async Task<T> InvokeOnUiAsync<T>(Func<Task<T>> work, TimeSpan timeout)
        {
            var app = WpfApplication.Current;
            Task<T> task;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
            {
                task = work();
            }
            else
            {
                task = app.Dispatcher.InvokeAsync(work).Task.Unwrap();
            }

            var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != task)
            {
                throw new TimeoutException($"UI work timed out after {timeout.TotalSeconds:0}s.");
            }

            return await task.ConfigureAwait(false);
        }
    }
}
