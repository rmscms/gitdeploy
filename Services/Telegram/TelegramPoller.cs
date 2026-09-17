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

        private readonly object _gate = new();
        private readonly TelegramChatStore _store = TelegramChatStore.Instance;
        private readonly TelegramBotClient _client = new();
        private readonly TelegramRouter _router;
        private readonly ConfigurationService _config = new();
        private readonly NotificationService _notifications = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private bool _disposed;
        private string _status = "";

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
                if (_loop != null && !_loop.IsCompleted)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunLoopAsync(token), token);
            }
        }

        public void Restart()
        {
            StopLoop();
            Start();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLoop();
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

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            SetStatus(Loc.T("telegram.statusStarting"));
            while (!cancellationToken.IsCancellationRequested)
            {
                var config = _config.LoadGlobalConfig();
                if (!config.TelegramEnabled)
                {
                    SetStatus(Loc.T("telegram.statusDisabled"));
                    await DelaySafe(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var token = EncryptionService.Decrypt(config.TelegramBotToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetStatus(Loc.T("telegram.statusNoToken"));
                    await DelaySafe(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    SetStatus(Loc.T("telegram.statusConnected"));
                    var offset = _store.GetLastUpdateId() + 1;
                    var updates = await _client.GetUpdatesAsync(token, offset, 25, cancellationToken).ConfigureAwait(false);
                    foreach (var update in updates)
                    {
                        if (update.UpdateId > 0)
                        {
                            _store.SetLastUpdateId(update.UpdateId);
                        }

                        await _router.HandleIncomingAsync(token, update, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus(Loc.T("telegram.statusError", ex.Message));
                    await DelaySafe(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                }
            }

            SetStatus(Loc.T("telegram.statusStopped"));
        }

        private void StopLoop()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate)
            {
                cts = _cts;
                loop = _loop;
                _cts = null;
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
                loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            cts?.Dispose();
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
