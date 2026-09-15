using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Vpn
{
    public interface IVpnProvider
    {
        string ProviderId { get; }
        string DisplayName { get; }
        string? ResolveExecutablePath(string? configuredPath);
        IReadOnlyList<VpnProfileInfo> ListProfiles(string? configuredGuiPath);
        Task ConnectAsync(string guiPath, string profileName, CancellationToken cancellationToken);
        Task DisconnectAsync(string guiPath, string profileName, CancellationToken cancellationToken);
        Task ReconnectAsync(string guiPath, string profileName, CancellationToken cancellationToken);
        Task<bool> IsLikelyConnectedAsync(string profileName, string? probeHost, CancellationToken cancellationToken);
    }

    public sealed class VpnProfileInfo
    {
        public string Name { get; init; } = "";
        public string FilePath { get; init; } = "";
        public string DisplayLabel { get; init; } = "";

        public override string ToString() =>
            string.IsNullOrWhiteSpace(DisplayLabel) ? Name : DisplayLabel;
    }

    public enum VpnConnectionState
    {
        Disabled,
        Idle,
        Connecting,
        Connected,
        Reconnecting,
        Down,
        Error
    }

    public sealed class VpnStatusChangedEventArgs : System.EventArgs
    {
        public VpnConnectionState State { get; init; }
        public string Message { get; init; } = "";
        public string ProfileName { get; init; } = "";
    }
}
