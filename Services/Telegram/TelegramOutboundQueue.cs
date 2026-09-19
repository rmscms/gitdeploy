using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;
using Newtonsoft.Json.Linq;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Serial outbound Telegram sender. Agent turns enqueue and continue immediately;
    /// network latency / retries never block Cursor ACP or -p workers.
    /// </summary>
    internal sealed class TelegramOutboundQueue : IDisposable
    {
        public static TelegramOutboundQueue Instance { get; } = new();

        private readonly ConcurrentQueue<OutboundJob> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly TelegramBotClient _client = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private int _disposed;

        private TelegramOutboundQueue()
        {
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        public void EnqueueText(
            string token,
            long chatId,
            string projectPath,
            string text,
            JToken? replyMarkup = null,
            string? parseMode = null)
        {
            if (Volatile.Read(ref _disposed) != 0
                || string.IsNullOrWhiteSpace(token)
                || chatId == 0
                || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _queue.Enqueue(new OutboundJob(
                token.Trim(),
                chatId,
                projectPath ?? string.Empty,
                text,
                replyMarkup,
                parseMode,
                ResetKeyboard: false,
                ResetHint: null,
                DocumentPath: null,
                DocumentCaption: null));
            _signal.Release();
        }

        public void EnqueueDocument(
            string token,
            long chatId,
            string projectPath,
            string documentPath,
            string? caption)
        {
            if (Volatile.Read(ref _disposed) != 0
                || string.IsNullOrWhiteSpace(token)
                || chatId == 0
                || string.IsNullOrWhiteSpace(documentPath)
                || !System.IO.File.Exists(documentPath))
            {
                return;
            }

            _queue.Enqueue(new OutboundJob(
                token.Trim(),
                chatId,
                projectPath ?? string.Empty,
                Text: string.Empty,
                ReplyMarkup: null,
                ParseMode: null,
                ResetKeyboard: false,
                ResetHint: null,
                DocumentPath: documentPath,
                DocumentCaption: caption));
            _signal.Release();
        }

        public void EnqueueKeyboardReset(
            string token,
            long chatId,
            string projectPath,
            string? hintText)
        {
            if (Volatile.Read(ref _disposed) != 0
                || string.IsNullOrWhiteSpace(token)
                || chatId == 0)
            {
                return;
            }

            _queue.Enqueue(new OutboundJob(
                token.Trim(),
                chatId,
                projectPath ?? string.Empty,
                Text: string.Empty,
                ReplyMarkup: null,
                ParseMode: null,
                ResetKeyboard: true,
                ResetHint: hintText,
                DocumentPath: null,
                DocumentCaption: null));
            _signal.Release();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            try
            {
                _signal.Release();
            }
            catch
            {
            }

            try
            {
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _signal.Dispose();
            _cts.Dispose();
            _client.Dispose();
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (_queue.TryDequeue(out var job))
                {
                    var ok = false;
                    Exception? lastError = null;
                    for (var attempt = 0; attempt < 2 && !ok; attempt++)
                    {
                        if (attempt > 0)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                break;
                            }
                        }

                        try
                        {
                            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            sendCts.CancelAfter(TimeSpan.FromSeconds(45));

                            if (!string.IsNullOrWhiteSpace(job.DocumentPath))
                            {
                                using var docCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                docCts.CancelAfter(TimeSpan.FromSeconds(90));
                                var messageId = await _client.SendDocumentAsync(
                                        job.Token,
                                        job.ChatId,
                                        job.DocumentPath!,
                                        job.DocumentCaption,
                                        docCts.Token)
                                    .ConfigureAwait(false);
                                if (messageId > 0 && !string.IsNullOrWhiteSpace(job.ProjectPath))
                                {
                                    TelegramChatStore.Instance.TrackBotMessageId(job.ProjectPath, messageId);
                                }
                            }
                            else if (job.ResetKeyboard)
                            {
                                await TelegramDeployCoordinator.ForceResetKeyboardAsync(
                                        job.Token,
                                        job.ChatId,
                                        job.ProjectPath,
                                        _client,
                                        sendCts.Token,
                                        job.ResetHint)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                string? parseMode = job.ParseMode;
                                var text = job.Text;
                                if (attempt > 0 && !string.IsNullOrWhiteSpace(job.ParseMode))
                                {
                                    // Bad HTML → plain text fallback (strip tags).
                                    parseMode = null;
                                    text = System.Text.RegularExpressions.Regex.Replace(
                                        job.Text ?? string.Empty,
                                        "<[^>]+>",
                                        string.Empty);
                                    text = System.Net.WebUtility.HtmlDecode(text);
                                }

                                var messageId = await _client.SendMessageAsync(
                                        job.Token,
                                        job.ChatId,
                                        text,
                                        sendCts.Token,
                                        job.ReplyMarkup,
                                        parseMode)
                                    .ConfigureAwait(false);

                                if (messageId > 0 && !string.IsNullOrWhiteSpace(job.ProjectPath))
                                {
                                    TelegramChatStore.Instance.TrackBotMessageId(job.ProjectPath, messageId);
                                }
                            }

                            ok = true;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                        }
                    }

                    if (!ok && !string.IsNullOrWhiteSpace(job.ProjectPath) && lastError != null)
                    {
                        try
                        {
                            var thread = TelegramChatStore.Instance.LoadThread(job.ProjectPath);
                            thread.Messages.Add(new TelegramChatMessage
                            {
                                Direction = TelegramMessageDirection.System,
                                Status = TelegramMessageStatus.Failed,
                                Text = Loc.T("telegram.sendQueueFailed", lastError.Message),
                                Utc = DateTime.UtcNow,
                                SenderName = "Telegram"
                            });
                            TelegramChatStore.Instance.SaveThread(thread);
                            TelegramPoller.Instance.RaiseMessage(job.ProjectPath, thread.Messages[^1]);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private readonly record struct OutboundJob(
            string Token,
            long ChatId,
            string ProjectPath,
            string Text,
            JToken? ReplyMarkup,
            string? ParseMode,
            bool ResetKeyboard,
            string? ResetHint,
            string? DocumentPath,
            string? DocumentCaption);
    }
}
