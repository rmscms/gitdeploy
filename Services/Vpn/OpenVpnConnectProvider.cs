using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Vpn
{
    /// <summary>
    /// Controls OpenVPN Connect 3.x via:
    /// OpenVPNConnect.exe --connect-shortcut=&lt;profileId&gt;
    /// OpenVPNConnect.exe --disconnect-shortcut
    /// Profiles are read from %APPDATA%\OpenVPN Connect\config.json.
    /// </summary>
    public sealed class OpenVpnConnectProvider : IVpnProvider
    {
        public string ProviderId => "OpenVpnConnect";
        public string DisplayName => "OpenVPN Connect";

        public string? ResolveExecutablePath(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath.Trim()))
            {
                return configuredPath.Trim();
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN Connect", "OpenVPNConnect.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN Connect", "OpenVPNConnect.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenVPN Connect", "OpenVPNConnect.exe")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return FindOnPath("OpenVPNConnect.exe");
        }

        public IReadOnlyList<VpnProfileInfo> ListProfiles(string? configuredExePath)
        {
            var byId = new Dictionary<string, VpnProfileInfo>(StringComparer.OrdinalIgnoreCase);
            TryAddFromConfigJson(byId);
            TryAddFromProfilesFolder(byId);
            return byId.Values
                .OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task ConnectAsync(string exePath, string profileName, CancellationToken cancellationToken)
        {
            var profileId = NormalizeProfileId(profileName);
            EnsureExe(exePath);
            // OpenVPN Connect ignores --connect-shortcut on a running instance.
            // Verified working path: stop processes, then cold-start with the flag.
            await StopOpenVpnConnectProcessesAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            await LaunchConnectAsync(exePath, $"--connect-shortcut={profileId}", cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task DisconnectAsync(string exePath, string profileName, CancellationToken cancellationToken)
        {
            EnsureExe(exePath);
            await StopOpenVpnConnectProcessesAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
            // Bring UI back in a disconnected state (same cold-start requirement).
            await LaunchConnectAsync(exePath, "--disconnect-shortcut", cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task ReconnectAsync(string exePath, string profileName, CancellationToken cancellationToken)
        {
            await ConnectAsync(exePath, profileName, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> IsLikelyConnectedAsync(
            string profileName,
            string? probeHost,
            CancellationToken cancellationToken)
        {
            var profileId = string.IsNullOrWhiteSpace(profileName)
                ? null
                : NormalizeProfileId(profileName, throwIfEmpty: false);

            if (!string.IsNullOrWhiteSpace(profileId) && IsConfigReportingConnected(profileId) && HasActiveVpnAdapter())
            {
                if (string.IsNullOrWhiteSpace(probeHost))
                {
                    return true;
                }
            }

            var hasAdapter = HasActiveVpnAdapter();
            var hasProcess = Process.GetProcessesByName("OpenVPNConnect").Length > 0
                             || Process.GetProcessesByName("ovpnhelper_service").Length > 0;
            if (!hasAdapter)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(probeHost))
            {
                return hasAdapter && (string.IsNullOrWhiteSpace(profileId) || IsConfigReportingConnected(profileId) || hasProcess);
            }

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(probeHost.Trim(), 2000).ConfigureAwait(false);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return hasAdapter;
            }
        }

        private static void TryAddFromConfigJson(Dictionary<string, VpnProfileInfo> byId)
        {
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpenVPN Connect",
                    "config.json");
                if (!File.Exists(configPath))
                {
                    return;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (!doc.RootElement.TryGetProperty("persist:root", out var persistRoot)
                    || persistRoot.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                using var rootDoc = JsonDocument.Parse(persistRoot.GetString() ?? "{}");
                if (!rootDoc.RootElement.TryGetProperty("status", out var statusRaw)
                    || statusRaw.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                using var statusDoc = JsonDocument.Parse(statusRaw.GetString() ?? "{}");
                if (!statusDoc.RootElement.TryGetProperty("profiles", out var profiles)
                    || profiles.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var prop in profiles.EnumerateObject())
                {
                    var id = prop.Name;
                    var name = ReadString(prop.Value, "profileDisplayName")
                               ?? ReadString(prop.Value, "name")
                               ?? ReadString(prop.Value, "profileName")
                               ?? id;
                    var filePath = ReadString(prop.Value, "filePath")
                                   ?? Path.Combine(
                                       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "OpenVPN Connect",
                                       "profiles",
                                       id + ".ovpn");

                    byId[id] = new VpnProfileInfo
                    {
                        Name = id,
                        FilePath = filePath,
                        DisplayLabel = $"{name}  (id {id})"
                    };
                }
            }
            catch
            {
                // Fall back to folder scan.
            }
        }

        private static void TryAddFromProfilesFolder(Dictionary<string, VpnProfileInfo> byId)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpenVPN Connect",
                    "profiles");
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.ovpn"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id))
                    {
                        continue;
                    }

                    byId[id] = new VpnProfileInfo
                    {
                        Name = id,
                        FilePath = file,
                        DisplayLabel = $"{id}  ({Path.GetFileName(file)})"
                    };
                }
            }
            catch
            {
            }
        }

        private static bool IsConfigReportingConnected(string profileId)
        {
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpenVPN Connect",
                    "config.json");
                if (!File.Exists(configPath))
                {
                    return false;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (!doc.RootElement.TryGetProperty("persist:root", out var persistRoot)
                    || persistRoot.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                using var rootDoc = JsonDocument.Parse(persistRoot.GetString() ?? "{}");
                if (!rootDoc.RootElement.TryGetProperty("status", out var statusRaw)
                    || statusRaw.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                using var statusDoc = JsonDocument.Parse(statusRaw.GetString() ?? "{}");
                if (!statusDoc.RootElement.TryGetProperty("connectedProfile", out var connected)
                    || connected.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var connectedId = ReadString(connected, "id");
                return string.Equals(connectedId, profileId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasActiveVpnAdapter()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    var desc = (nic.Description ?? string.Empty) + " " + (nic.Name ?? string.Empty);
                    if (desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("TAP-Win32", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("ovpn", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                    {
                        var props = nic.GetIPProperties();
                        if (props.UnicastAddresses.Any(a =>
                                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void EnsureExe(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                throw new InvalidOperationException("OpenVPNConnect.exe was not found.");
            }
        }

        private static string NormalizeProfileId(string profileName, bool throwIfEmpty = true)
        {
            var name = (profileName ?? string.Empty).Trim();
            if (name.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                if (throwIfEmpty)
                {
                    throw new InvalidOperationException("VPN profile is not selected.");
                }

                return string.Empty;
            }

            return name;
        }

        private static async Task StopOpenVpnConnectProcessesAsync(CancellationToken cancellationToken)
        {
            foreach (var name in new[] { "OpenVPNConnect", "agent_ovpnconnect" })
            {
                Process[] list;
                try
                {
                    list = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var process in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            // Wait until processes are gone (or timeout).
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var still =
                    Process.GetProcessesByName("OpenVPNConnect").Length > 0;
                if (!still)
                {
                    return;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task LaunchConnectAsync(
            string exePath,
            string arguments,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("Could not start OpenVPNConnect.exe.");

            try
            {
                // Give Connect time to boot and apply the connect/disconnect action.
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static string? FindOnPath(string fileName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim().Trim('"'), fileName);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                }
            }

            return null;
        }
    }
}
