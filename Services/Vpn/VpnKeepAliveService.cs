using System;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Services;

namespace GitDeployPro.Services.Vpn
{
    /// <summary>
    /// Keeps the configured VPN profile connected while GitDeployPro is running.
    /// </summary>
    public sealed class VpnKeepAliveService : IDisposable
    {
        public static VpnKeepAliveService Instance { get; } = new();

        private readonly ConfigurationService _config = new();
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _reconnectAttempts;
        private DateTime _nextReconnectUtc = DateTime.MinValue;
        private VpnConnectionState _state = VpnConnectionState.Disabled;
        private string _statusMessage = "";
        private bool _disposed;

        public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;

        public VpnConnectionState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public string StatusMessage
        {
            get
            {
                lock (_gate)
                {
                    return _statusMessage;
                }
            }
        }

        public void StartFromConfig()
        {
            if (_disposed)
            {
                return;
            }

            StopMonitor();
            var cfg = _config.LoadGlobalConfig();
            if (!cfg.VpnEnabled)
            {
                SetStatus(VpnConnectionState.Disabled, "VPN keep-alive is off.");
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(cfg, _cts.Token));
        }

        public void ApplyConfigAndRestart()
        {
            StartFromConfig();
        }

        public async Task ConnectNowAsync(CancellationToken cancellationToken = default)
        {
            var cfg = _config.LoadGlobalConfig();
            await ConnectInternalAsync(cfg, cancellationToken).ConfigureAwait(false);
            if (cfg.VpnEnabled && (_cts == null || _cts.IsCancellationRequested))
            {
                StartFromConfig();
            }
        }

        public async Task DisconnectNowAsync(CancellationToken cancellationToken = default)
        {
            var cfg = _config.LoadGlobalConfig();
            var provider = VpnProviderFactory.Create(cfg.VpnProvider);
            var exe = provider.ResolveExecutablePath(VpnProviderFactory.ResolveConfiguredPath(cfg))
                      ?? throw new InvalidOperationException($"{provider.DisplayName} executable was not found.");
            SetStatus(VpnConnectionState.Idle, "Disconnecting…");
            await provider.DisconnectAsync(exe, cfg.VpnProfileName, cancellationToken).ConfigureAwait(false);
            SetStatus(VpnConnectionState.Down, "Disconnected.");
        }

        public async Task ReconnectNowAsync(CancellationToken cancellationToken = default)
        {
            var cfg = _config.LoadGlobalConfig();
            var provider = VpnProviderFactory.Create(cfg.VpnProvider);
            var exe = provider.ResolveExecutablePath(VpnProviderFactory.ResolveConfiguredPath(cfg))
                      ?? throw new InvalidOperationException($"{provider.DisplayName} executable was not found.");
            SetStatus(VpnConnectionState.Reconnecting, "Reconnecting…");
            try
            {
                await provider.ReconnectAsync(exe, cfg.VpnProfileName, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await provider.ConnectAsync(exe, cfg.VpnProfileName, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            var ok = await provider
                .IsLikelyConnectedAsync(cfg.VpnProfileName, cfg.VpnHealthProbeHost, cancellationToken)
                .ConfigureAwait(false);
            SetStatus(
                ok ? VpnConnectionState.Connected : VpnConnectionState.Down,
                ok ? "Connected." : "Still down after reconnect.");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopMonitor();
        }

        private void StopMonitor()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }

        private async Task RunAsync(ConfigurationService.GlobalConfig startupCfg, CancellationToken cancellationToken)
        {
            try
            {
                var provider = VpnProviderFactory.Create(startupCfg.VpnProvider);
                // Always probe first: VPN may already be up (manual connect / previous session).
                // OpenVPN Connect's Connect kills running processes — never reconnect blindly.
                var alreadyUp = await provider
                    .IsLikelyConnectedAsync(startupCfg.VpnProfileName, startupCfg.VpnHealthProbeHost, cancellationToken)
                    .ConfigureAwait(false);
                if (alreadyUp)
                {
                    SetStatus(VpnConnectionState.Connected, "VPN already connected — skipped startup connect.");
                }
                else if (startupCfg.VpnConnectOnStartup)
                {
                    await ConnectInternalAsync(startupCfg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SetStatus(VpnConnectionState.Idle, "Monitoring (connect-on-startup off).");
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var cfg = _config.LoadGlobalConfig();
                    if (!cfg.VpnEnabled)
                    {
                        SetStatus(VpnConnectionState.Disabled, "VPN keep-alive is off.");
                        break;
                    }

                    provider = VpnProviderFactory.Create(cfg.VpnProvider);
                    var interval = Math.Clamp(cfg.VpnHealthIntervalSeconds <= 0 ? 30 : cfg.VpnHealthIntervalSeconds, 10, 600);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    cfg = _config.LoadGlobalConfig();
                    if (!cfg.VpnEnabled)
                    {
                        break;
                    }

                    provider = VpnProviderFactory.Create(cfg.VpnProvider);
                    var connected = await provider
                        .IsLikelyConnectedAsync(cfg.VpnProfileName, cfg.VpnHealthProbeHost, cancellationToken)
                        .ConfigureAwait(false);
                    if (connected)
                    {
                        _reconnectAttempts = 0;
                        _nextReconnectUtc = DateTime.MinValue;
                        SetStatus(VpnConnectionState.Connected, "Connected.");
                        continue;
                    }

                    SetStatus(VpnConnectionState.Down, "VPN appears down.");
                    if (!cfg.VpnAutoReconnect)
                    {
                        continue;
                    }

                    if (cfg.VpnMaxReconnectAttempts > 0 && _reconnectAttempts >= cfg.VpnMaxReconnectAttempts)
                    {
                        SetStatus(VpnConnectionState.Error, "Reconnect attempts exhausted.");
                        continue;
                    }

                    if (DateTime.UtcNow < _nextReconnectUtc)
                    {
                        continue;
                    }

                    _reconnectAttempts++;
                    var delaySec = Math.Min(120, 15 * (1 << Math.Min(_reconnectAttempts - 1, 3)));
                    _nextReconnectUtc = DateTime.UtcNow.AddSeconds(delaySec);
                    SetStatus(
                        VpnConnectionState.Reconnecting,
                        $"Reconnecting (attempt {_reconnectAttempts})…");
                    try
                    {
                        await ReconnectNowAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(VpnConnectionState.Error, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetStatus(VpnConnectionState.Error, ex.Message);
            }
        }

        private async Task ConnectInternalAsync(
            ConfigurationService.GlobalConfig cfg,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cfg.VpnProfileName))
            {
                SetStatus(VpnConnectionState.Error, "No VPN profile selected.");
                return;
            }

            var provider = VpnProviderFactory.Create(cfg.VpnProvider);
            var exe = provider.ResolveExecutablePath(VpnProviderFactory.ResolveConfiguredPath(cfg));
            if (string.IsNullOrWhiteSpace(exe))
            {
                SetStatus(VpnConnectionState.Error, $"{provider.DisplayName} executable was not found.");
                return;
            }

            // Do not kill/restart an already-working tunnel (esp. OpenVPN Connect).
            var alreadyUp = await provider
                .IsLikelyConnectedAsync(cfg.VpnProfileName, cfg.VpnHealthProbeHost, cancellationToken)
                .ConfigureAwait(false);
            if (alreadyUp)
            {
                SetStatus(VpnConnectionState.Connected, "Already connected.");
                return;
            }

            SetStatus(VpnConnectionState.Connecting, $"Connecting {cfg.VpnProfileName}…");
            try
            {
                await provider.ConnectAsync(exe, cfg.VpnProfileName, cancellationToken).ConfigureAwait(false);
                // Connect GUI needs a few seconds to bring the tunnel up.
                var waitMs = VpnProviderFactory.NormalizeProviderId(cfg.VpnProvider) == VpnProviderFactory.OpenVpnConnect
                    ? 6000
                    : 3000;
                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                var ok = await provider
                    .IsLikelyConnectedAsync(cfg.VpnProfileName, cfg.VpnHealthProbeHost, cancellationToken)
                    .ConfigureAwait(false);
                SetStatus(
                    ok ? VpnConnectionState.Connected : VpnConnectionState.Connecting,
                    ok ? "Connected." : "Connect command sent — waiting for tunnel…");
            }
            catch (Exception ex)
            {
                SetStatus(VpnConnectionState.Error, ex.Message);
            }
        }

        private void SetStatus(VpnConnectionState state, string message)
        {
            string profile;
            lock (_gate)
            {
                _state = state;
                _statusMessage = message ?? string.Empty;
                profile = _config.LoadGlobalConfig().VpnProfileName ?? string.Empty;
            }

            try
            {
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs
                {
                    State = state,
                    Message = message ?? string.Empty,
                    ProfileName = profile
                });
            }
            catch
            {
            }
        }
    }
}
