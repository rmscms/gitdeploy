using System;

namespace GitDeployPro.Services.Vpn
{
    public static class VpnProviderFactory
    {
        public const string OpenVpnGui = "OpenVpn";
        public const string OpenVpnConnect = "OpenVpnConnect";

        public static IVpnProvider Create(string? providerId)
        {
            if (string.Equals(providerId, OpenVpnGui, StringComparison.OrdinalIgnoreCase))
            {
                return new OpenVpnGuiProvider();
            }

            return new OpenVpnConnectProvider();
        }

        public static string NormalizeProviderId(string? providerId)
        {
            if (string.Equals(providerId, OpenVpnGui, StringComparison.OrdinalIgnoreCase))
            {
                return OpenVpnGui;
            }

            return OpenVpnConnect;
        }

        public static string ResolveConfiguredPath(ConfigurationService.GlobalConfig cfg)
        {
            var provider = NormalizeProviderId(cfg.VpnProvider);
            if (provider == OpenVpnGui)
            {
                return cfg.VpnOpenVpnGuiPath ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(cfg.VpnOpenVpnConnectPath)
                ? (cfg.VpnOpenVpnGuiPath ?? string.Empty) // legacy single-path fallback
                : cfg.VpnOpenVpnConnectPath;
        }
    }
}
