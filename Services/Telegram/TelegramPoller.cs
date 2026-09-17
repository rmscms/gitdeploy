using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    public sealed class TelegramPoller : IDisposable
    {
        public static TelegramPoller Instance { get; } = new();

        /// <summary>If getUpdates / poll cycle does not progress for this long, restart the loop.</summary>
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(90);

        private static readonly TimeSpan MinRestartGap = TimeSpan.FromSeconds(40);

        private readonly object _gate = new();
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly TelegramBotClient _client = new();
        private readonly TelegramRouter _router;
        private readonly ConfigurationService _config = new();
        private readonly NotificationService _notifications = new();

        private CancellationTokenSource? _lifetimeCts;
        private CancellationTokenSource? _loopCts;
        private Task? _loop;
        private Task? _watchdog;
        private bool _disposed;
        private string _status = "";
        private long _lastProgressUtcTicks = DateTime.UtcNow.Ticks;
        private long _lastRestartUtcTicks = DateTime.MinValue.Ticks;
        private int _restarting;

        public event EventHandler<TelegramMessageEventArgs>? MessageReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ThreadCleared;

        public bool ChatPageVisible { get; set; }

        public string StatusText
        {
            get
            {
                lock (_gate)
                {
                    return _status;
                }
            }
        }

        private TelegramPoller()
        {
            _router = new TelegramRouter(_store, _client);
            _status = Loc.T("telegram.statusStopped");
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                _lifetimeCts ??= new CancellationTokenSource();
                EnsureLoopUnlocked();
                if (_watchdog == null || _watchdog.IsCompleted)
                {
                    var life = _lifetimeCts.Token;
                    _watchdog = Task.Run(() => WatchdogLoopAsync(life), life);
                }
            }
        }

        public void Restart()
        {
            if (_disposed)
            {
                return;
            }

            RestartLoop(Loc.T("telegram.statusRestarting"));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _lifetimeCts?.Cancel();
            }
            catch
            {
            }

            StopLoop();
            try
            {
                _watchdog?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _client.Dispose();
        }

        public async Task<(bool Ok, string Message)> TestConnectionAsync(string? plainToken, CancellationToken cancellationToken)
        {
            var token = ResolveToken(plainToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, Loc.T("telegram.missingToken"));
            }

            try
            {
                var result = await _client.GetMeAsync(token, cancellationToken).ConfigureAwait(false);
                if (!result.Ok)
                {
                    return (false, result.Description);
                }

                var username = result.Result?["username"]?.ToString();
                var name = string.IsNullOrWhiteSpace(username)
                    ? Loc.T("telegram.testOk")
                    : Loc.T("telegram.testOkNamed", username);
                return (true, name);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task SendOutgoingAsync(string projectPath, string text, string? photoPath, CancellationToken cancellationToken)
        {
            var config = _config.LoadGlobalConfig();
            var token = EncryptionService.Decrypt(config.TelegramBotToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(Loc.T("telegram.missingToken"));
            }

            var chatId = _store.GetLastChatId();
            if (chatId == 0)
            {
                throw new InvalidOperationException(Loc.T("telegram.noChatYet"));
            }

            var hasPhoto = !string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath);
            if (hasPhoto)
            {
                await _client.SendPhotoAsync(token, chatId, photoPath!, text, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException(Loc.T("telegram.emptyMessage"));
                }

                await _client.SendMessageAsync(token, chatId, text, cancellationToken).ConfigureAwait(false);
            }

            var storedPhoto = string.Empty;
            if (hasPhoto)
            {
                storedPhoto = CopyOutgoingPhoto(projectPath, photoPath!);
            }

            var message = new TelegramChatMessage
            {
                Direction = TelegramMessageDirection.Outgoing,
                Status = TelegramMessageStatus.Sent,
                Text = text ?? string.Empty,
                PhotoPath = storedPhoto,
                ChatId = chatId,
                Utc = DateTime.UtcNow
            };
            _store.Append(projectPath, message);
            RaiseMessage(projectPath, message);
            AgentFacade.EnqueueUserTurn(projectPath, text ?? string.Empty, storedPhoto);
        }

        public void RaiseMessage(string projectPath, TelegramChatMessage message)
        {
            MessageReceived?.Invoke(this, new TelegramMessageEventArgs
            {
                ProjectPath = projectPath,
                Message = message
            });

            if (!ChatPageVisible && message.Direction == TelegramMessageDirection.Incoming)
            {
                var preview = TelegramChatStore.BuildPreview(message);
                _notifications.ShowToast(TelegramPaths.DisplayName(projectPath), preview);
            }
        }

        public void RaiseThreadCleared(string projectPath)
        {
            ThreadCleared?.Invoke(this, projectPath ?? string.Empty);
        }

        private void EnsureLoopUnlocked()
        {
            if (_loop != null && !_loop.IsCompleted)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            MarkProgress();
            _loop = Task.Run(() => RunLoopAsync(token), token);
        }

        private void RestartLoop(string status)
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var lastRestart = Interlocked.Read(ref _lastRestartUtcTicks);
                if (lastRestart > 0
                    && new DateTime(lastRestart, DateTimeKind.Utc) + MinRestartGap > DateTime.UtcNow)
                {
                    return;
                }

                Interlocked.Exchange(ref _lastRestartUtcTicks, nowTicks);
                SetStatus(status);
                StopLoop();
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        EnsureLoopUnlocked();
                    }
                }

                MarkProgress();
            }
            finally
            {
                Interlocked.Exchange(ref _restarting, 0);
            }
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            SetStatus(Loc.T("telegram.statusStarting"));
            MarkProgress();
            while (!cancellationToken.IsCancellationRequested)
            {
                var config = _config.LoadGlobalConfig();
                if (!config.TelegramEnabled)
                {
                    SetStatus(Loc.T("telegram.statusDisabled"));
                    MarkProgress();
                    await DelaySafe(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetStatus(Loc.T("telegram.statusNoToken"));
                    MarkProgress();
                    await DelaySafe(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    SetStatus(Loc.T("telegram.statusConnected"));
                    MarkProgress();
                    var offset = _store.GetLastUpdateId() + 1;
                    var updates = await _client.GetUpdatesAsync(token, offset, 25, cancellationToken)
                        .ConfigureAwait(false);
                    MarkProgress();

                    foreach (var update in updates)
                    {
                        if (update.UpdateId > 0)
                        {
                            _store.SetLastUpdateId(update.UpdateId);
                        }

                        // Never block long-poll on Preview/Deploy/Model/wipe/agent work.
                        DispatchIncoming(token, update, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MarkProgress();
                    SetStatus(Loc.T("telegram.statusError", Truncate(ex.Message, 120)));
                    await DelaySafe(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
            }

            SetStatus(Loc.T("telegram.statusStopped"));
        }

        private void DispatchIncoming(string token, TelegramIncomingUpdate update, CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _router.HandleIncomingAsync(token, update, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    try
                    {
                        var project = _store.GetActiveProjectPath();
                        var msg = new TelegramChatMessage
                        {
                            Direction = TelegramMessageDirection.System,
                            Status = TelegramMessageStatus.Failed,
                            Text = Loc.T("telegram.handlerFailed", Truncate(ex.Message, 160)),
                            Utc = DateTime.UtcNow,
                            SenderName = "Telegram"
                        };
                        if (!string.IsNullOrWhiteSpace(project) && !TelegramPaths.IsUnassigned(project))
                        {
                            _store.Append(project, msg);
                            RaiseMessage(project, msg);
                        }
                    }
                    catch
                    {
                    }
                }
            }, cancellationToken);
        }

        private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await DelaySafe(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || _disposed)
                {
                    break;
                }

                var config = _config.LoadGlobalConfig();
                if (!config.TelegramEnabled)
                {
                    MarkProgress();
                    continue;
                }

                var last = new DateTime(Interlocked.Read(ref _lastProgressUtcTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - last < StallLimit)
                {
                    continue;
                }

                // Stuck getUpdates / dead loop — recover without full app restart.
                RestartLoop(Loc.T("telegram.statusWatchdogRestart"));
            }
        }

        private void StopLoop()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate)
            {
                cts = _loopCts;
                loop = _loop;
                _loopCts = null;
                _loop = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                loop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            cts?.Dispose();
        }

        private void MarkProgress()
        {
            Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);
        }

        private void SetStatus(string status)
        {
            lock (_gate)
            {
                if (string.Equals(_status, status, StringComparison.Ordinal))
                {
                    return;
                }

                _status = status;
            }

            StatusChanged?.Invoke(this, status);
        }

        private static string ResolveToken(string? plainToken)
        {
            if (!string.IsNullOrWhiteSpace(plainToken))
            {
                return plainToken.Trim();
            }

            var stored = new ConfigurationService().LoadGlobalConfig().TelegramBotToken;
            return EncryptionService.Decrypt(stored);
        }

        private static string CopyOutgoingPhoto(string projectPath, string sourcePath)
        {
            try
            {
                var dest = Path.Combine(
                    TelegramPaths.MediaFolder(projectPath),
                    Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
                File.Copy(sourcePath, dest, overwrite: true);
                return dest;
            }
            catch
            {
                return sourcePath;
            }
        }

        private static string Truncate(string? text, int max)
        {
            text ??= string.Empty;
            return text.Length <= max ? text : text[..max] + "…";
        }

        private static async Task DelaySafe(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
