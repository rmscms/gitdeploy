using System;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using GitDeployPro.Services.Vpn;

namespace GitDeployPro.Services
{
    /// <summary>
    /// Connects OpenVPN for a schedule when required; disconnects only if this scope opened the tunnel.
    /// </summary>
    public sealed class BackupVpnScope : IAsyncDisposable
    {
        private readonly IVpnProvider _provider;
        private readonly string _exePath;
        private readonly string _profileName;
        private readonly string? _probeHost;
        private readonly bool _weConnected;
        private bool _disposed;

        private BackupVpnScope(
            IVpnProvider provider,
            string exePath,
            string profileName,
            string? probeHost,
            bool weConnected)
        {
            _provider = provider;
            _exePath = exePath;
            _profileName = profileName;
            _probeHost = probeHost;
            _weConnected = weConnected;
        }

        public static async Task<BackupVpnScope?> EnterAsync(
            BackupSchedule schedule,
            ConfigurationService.GlobalConfig? globalConfig = null,
            CancellationToken cancellationToken = default)
        {
            if (schedule == null || !schedule.VpnRequired)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(schedule.VpnProfileName))
            {
                throw new InvalidOperationException(
                    $"Schedule \"{schedule.Name}\" requires VPN but no profile is selected.");
            }

            var cfg = globalConfig ?? new ConfigurationService().LoadGlobalConfig();
            var providerId = VpnProviderFactory.NormalizeProviderId(
                string.IsNullOrWhiteSpace(schedule.VpnProvider) ? cfg.VpnProvider : schedule.VpnProvider);
            var provider = VpnProviderFactory.Create(providerId);

            // Prefer schedule provider path from global config slots.
            var pathCfg = cfg;
            if (!string.Equals(providerId, VpnProviderFactory.NormalizeProviderId(cfg.VpnProvider), StringComparison.OrdinalIgnoreCase))
            {
                // Still use global paths for whichever client is selected.
            }

            var exe = provider.ResolveExecutablePath(VpnProviderFactory.ResolveConfiguredPath(CloneWithProvider(cfg, providerId)))
                      ?? throw new InvalidOperationException(
                          $"{provider.DisplayName} executable was not found. Configure the path in Settings → VPN.");

            var alreadyUp = await provider
                .IsLikelyConnectedAsync(schedule.VpnProfileName.Trim(), cfg.VpnHealthProbeHost, cancellationToken)
                .ConfigureAwait(false);

            if (alreadyUp)
            {
                return new BackupVpnScope(provider, exe, schedule.VpnProfileName.Trim(), cfg.VpnHealthProbeHost, weConnected: false);
            }

            await provider.ConnectAsync(exe, schedule.VpnProfileName.Trim(), cancellationToken).ConfigureAwait(false);
            await Task.Delay(2500, cancellationToken).ConfigureAwait(false);

            var ok = await provider
                .IsLikelyConnectedAsync(schedule.VpnProfileName.Trim(), cfg.VpnHealthProbeHost, cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"VPN profile \"{schedule.VpnProfileName}\" did not come up in time.");
            }

            return new BackupVpnScope(provider, exe, schedule.VpnProfileName.Trim(), cfg.VpnHealthProbeHost, weConnected: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_weConnected)
            {
                return;
            }

            try
            {
                await _provider.DisconnectAsync(_exePath, _profileName, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort teardown after backup.
            }
        }

        private static ConfigurationService.GlobalConfig CloneWithProvider(
            ConfigurationService.GlobalConfig cfg,
            string providerId)
        {
            // ResolveConfiguredPath only needs VpnProvider + path fields.
            return new ConfigurationService.GlobalConfig
            {
                VpnProvider = providerId,
                VpnOpenVpnGuiPath = cfg.VpnOpenVpnGuiPath,
                VpnOpenVpnConnectPath = cfg.VpnOpenVpnConnectPath,
                VpnProfileName = cfg.VpnProfileName,
                VpnHealthProbeHost = cfg.VpnHealthProbeHost
            };
        }
    }
}
