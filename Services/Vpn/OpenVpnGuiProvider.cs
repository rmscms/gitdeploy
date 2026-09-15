using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Vpn
{
    /// <summary>
    /// Controls community OpenVPN GUI via official CLI switches.
    /// </summary>
    public sealed class OpenVpnGuiProvider : IVpnProvider
    {
        public string ProviderId => "OpenVpn";
        public string DisplayName => "OpenVPN";

        public string? ResolveExecutablePath(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath.Trim()))
            {
                return configuredPath.Trim();
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn-gui.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn-gui.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenVPN", "bin", "openvpn-gui.exe")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return FindOnPath("openvpn-gui.exe");
        }

        public IReadOnlyList<VpnProfileInfo> ListProfiles(string? configuredGuiPath)
        {
            var roots = new List<string>();
            var userConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OpenVPN",
                "config");
            roots.Add(userConfig);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "config"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "config"));

            var gui = ResolveExecutablePath(configuredGuiPath);
            if (!string.IsNullOrWhiteSpace(gui))
            {
                var bin = Path.GetDirectoryName(gui);
                var installRoot = Directory.GetParent(bin ?? string.Empty)?.FullName;
                if (!string.IsNullOrWhiteSpace(installRoot))
                {
                    roots.Add(Path.Combine(installRoot, "config"));
                }
            }

            var byName = new Dictionary<string, VpnProfileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*.ovpn", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name) || byName.ContainsKey(name))
                    {
                        continue;
                    }

                    byName[name] = new VpnProfileInfo
                    {
                        Name = name,
                        FilePath = file,
                        DisplayLabel = $"{name}  ({Path.GetFileName(file)})"
                    };
                }
            }

            return byName.Values
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task ConnectAsync(string guiPath, string profileName, CancellationToken cancellationToken)
        {
            var profile = NormalizeProfileName(profileName);
            EnsureGui(guiPath);
            // --command connect falls back to --connect when no GUI instance is running.
            return RunGuiAsync(
                guiPath,
                new[] { "--silent_connection", "1", "--command", "connect", profile },
                cancellationToken);
        }

        public Task DisconnectAsync(string guiPath, string profileName, CancellationToken cancellationToken)
        {
            var profile = NormalizeProfileName(profileName);
            EnsureGui(guiPath);
            return RunGuiAsync(guiPath, new[] { "--command", "disconnect", profile }, cancellationToken);
        }

        public Task ReconnectAsync(string guiPath, string profileName, CancellationToken cancellationToken)
        {
            var profile = NormalizeProfileName(profileName);
            EnsureGui(guiPath);
            return RunGuiAsync(guiPath, new[] { "--command", "reconnect", profile }, cancellationToken);
        }

        public async Task<bool> IsLikelyConnectedAsync(
            string profileName,
            string? probeHost,
            CancellationToken cancellationToken)
        {
            var hasVpnAdapter = HasActiveVpnAdapter();
            var hasOpenVpnProcess = Process.GetProcessesByName("openvpn").Length > 0
                                    || Process.GetProcessesByName("openvpn-gui").Length > 0;
            if (!hasVpnAdapter && !hasOpenVpnProcess)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(probeHost))
            {
                return hasVpnAdapter;
            }

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(probeHost.Trim(), 2000).ConfigureAwait(false);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return hasVpnAdapter;
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

                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    {
                        // Tunnel often used by VPN; keep checking description too.
                    }

                    var desc = (nic.Description ?? string.Empty) + " " + (nic.Name ?? string.Empty);
                    if (desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("TAP-Win32", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                        || desc.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
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

        private static void EnsureGui(string guiPath)
        {
            if (string.IsNullOrWhiteSpace(guiPath) || !File.Exists(guiPath))
            {
                throw new InvalidOperationException("openvpn-gui.exe was not found.");
            }
        }

        private static string NormalizeProfileName(string profileName)
        {
            var name = (profileName ?? string.Empty).Trim();
            if (name.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("VPN profile is not selected.");
            }

            return name;
        }

        private static async Task RunGuiAsync(string guiPath, string[] args, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo
            {
                FileName = guiPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(guiPath) ?? Environment.CurrentDirectory
            };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start openvpn-gui.exe.");
            }

            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(8000, cancellationToken))
                .ConfigureAwait(false);
            if (completed == exitTask)
            {
                await exitTask.ConfigureAwait(false);
            }
            // else: GUI likely stayed in tray — expected for connect.
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
