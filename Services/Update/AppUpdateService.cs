using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GitDeployPro.Services.Update
{
    public sealed class AppUpdateService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly HttpClient DownloadHttp = CreateDownloadClient();
        private readonly ConfigurationService _configService;

        public AppUpdateService(ConfigurationService? configService = null)
        {
            _configService = configService ?? new ConfigurationService();
        }

        public Version GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var normalized = NormalizeVersionString(informational);
                if (Version.TryParse(normalized, out var fromInfo))
                {
                    return fromInfo;
                }
            }

            var version = assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }

        public bool ShouldCheckAutomatically(DateTime? lastCheckUtc = null)
        {
            if (!UpdateOptions.IsConfigured)
            {
                return false;
            }

            lastCheckUtc ??= _configService.LoadGlobalConfig().LastUpdateCheckUtc;
            if (!lastCheckUtc.HasValue)
            {
                return true;
            }

            var elapsed = AppTimeService.UtcNow - lastCheckUtc.Value.ToUniversalTime();
            return elapsed >= TimeSpan.FromHours(UpdateOptions.CheckIntervalHours);
        }

        public void MarkCheckCompleted()
        {
            _configService.UpdateGlobalConfig(cfg => cfg.LastUpdateCheckUtc = AppTimeService.UtcNow);
        }

        public string GetInstallPathDisplay() => AppInstallPaths.ExecutablePath;

        public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            if (!UpdateOptions.IsConfigured)
            {
                return UpdateCheckResult.Failed("Update server is not configured for this app.");
            }

            try
            {
                var manifestUrl = CombineUrl(UpdateOptions.BaseUrl, "latest.json");
                using var response = await Http.GetAsync(manifestUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.Failed($"Update server returned {(int)response.StatusCode}.");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                {
                    return UpdateCheckResult.Failed("Invalid latest.json from update server.");
                }

                if (string.IsNullOrWhiteSpace(manifest.Sha256) ||
                    (string.IsNullOrWhiteSpace(manifest.FileName) && string.IsNullOrWhiteSpace(manifest.DownloadUrl)))
                {
                    return UpdateCheckResult.Failed("latest.json is missing required fields (sha256 / file).");
                }

                var current = GetCurrentVersion();
                if (!Version.TryParse(NormalizeVersionString(manifest.Version), out var remote))
                {
                    return UpdateCheckResult.Failed($"Invalid remote version: {manifest.Version}");
                }

                MarkCheckCompleted();

                if (remote <= current)
                {
                    return new UpdateCheckResult
                    {
                        IsUpdateAvailable = false,
                        CurrentVersion = current,
                        RemoteVersion = remote,
                        Manifest = manifest
                    };
                }

                return new UpdateCheckResult
                {
                    IsUpdateAvailable = true,
                    IsMandatory = manifest.Mandatory,
                    CurrentVersion = current,
                    RemoteVersion = remote,
                    Manifest = manifest
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failed(ex.Message);
            }
        }

        public PendingUpdateState? GetPendingUpdate()
        {
            try
            {
                var path = AppInstallPaths.PendingManifestPath;
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                var state = JsonConvert.DeserializeObject<PendingUpdateState>(json);
                if (state == null ||
                    string.IsNullOrWhiteSpace(state.PackagePath) ||
                    !File.Exists(state.PackagePath))
                {
                    return null;
                }

                return state;
            }
            catch
            {
                return null;
            }
        }

        public void ClearPendingUpdate()
        {
            try
            {
                if (File.Exists(AppInstallPaths.PendingManifestPath))
                {
                    File.Delete(AppInstallPaths.PendingManifestPath);
                }

                if (File.Exists(AppInstallPaths.PendingPackagePath))
                {
                    File.Delete(AppInstallPaths.PendingPackagePath);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Downloads and verifies the package while the app stays running.
        /// Does not replace the running EXE.
        /// </summary>
        public async Task<PendingUpdateState> DownloadOnlyAsync(
            UpdateManifest manifest,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!UpdateOptions.IsConfigured)
            {
                throw new InvalidOperationException("Update server is not configured.");
            }

            var fileName = string.IsNullOrWhiteSpace(manifest.FileName)
                ? Path.GetFileName(manifest.DownloadUrl)
                : manifest.FileName.Trim();
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            {
                throw new InvalidOperationException("Invalid update file name.");
            }

            Directory.CreateDirectory(AppInstallPaths.UpdateStagingDirectory);
            var packagePath = AppInstallPaths.PendingPackagePath;
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            var downloadUrl = ResolveDownloadUrl(manifest.DownloadUrl, fileName);
            await DownloadFileAsync(downloadUrl, packagePath, progress, cancellationToken);
            await VerifySha256Async(packagePath, manifest.Sha256, cancellationToken);

            var state = new PendingUpdateState
            {
                Version = manifest.Version?.Trim() ?? string.Empty,
                PackagePath = packagePath,
                Sha256 = manifest.Sha256?.Trim() ?? string.Empty,
                FileName = fileName,
                Mandatory = manifest.Mandatory,
                ReleaseNotes = manifest.ReleaseNotes ?? string.Empty
            };

            var json = JsonConvert.SerializeObject(state, Formatting.Indented);
            await File.WriteAllTextAsync(AppInstallPaths.PendingManifestPath, json, Encoding.UTF8, cancellationToken);
            return state;
        }

        /// <summary>
        /// Applies a previously downloaded package into the stable LocalAppData EXE and relaunches.
        /// Caller should shut down the current process after this returns.
        /// </summary>
        public async Task ApplyPendingAndRestartAsync(CancellationToken cancellationToken = default)
        {
            var pending = GetPendingUpdate()
                ?? throw new InvalidOperationException("No pending update package was found.");

            Directory.CreateDirectory(AppInstallPaths.InstallDirectory);
            var destExe = AppInstallPaths.ExecutablePath;
            var global = _configService.LoadGlobalConfig();
            var autoStart = new AutoStartService();

            // Portable / non-install process can write the install EXE immediately.
            if (!AppInstallPaths.IsRunningFromInstallPath())
            {
                File.Copy(pending.PackagePath, destExe, overwrite: true);
                DesktopShortcutService.EnsureShortcut(destExe);
                autoStart.RefreshToInstallPath(global.LaunchOnStartup || autoStart.IsEnabled());
                Process.Start(new ProcessStartInfo
                {
                    FileName = destExe,
                    UseShellExecute = true,
                    WorkingDirectory = AppInstallPaths.InstallDirectory
                });
                return;
            }

            // Running from install path: replace after exit via helper.
            autoStart.RefreshToInstallPath(global.LaunchOnStartup || autoStart.IsEnabled());
            DesktopShortcutService.EnsureShortcut(destExe);

            var helperDir = AppInstallPaths.UpdateStagingDirectory;
            Directory.CreateDirectory(helperDir);
            var helperPath = Path.Combine(helperDir, "apply-update.cmd");
            var pid = Environment.ProcessId;
            var helper = BuildHelperScript(pid, pending.PackagePath, destExe);
            await File.WriteAllTextAsync(helperPath, helper, Encoding.ASCII, cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{helperPath}\"\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = helperDir
            });
        }

        /// <summary>
        /// Legacy blocking path: download then apply immediately.
        /// Prefer <see cref="DownloadOnlyAsync"/> + <see cref="ApplyPendingAndRestartAsync"/>.
        /// </summary>
        public async Task DownloadAndApplyAsync(
            UpdateManifest manifest,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await DownloadOnlyAsync(manifest, progress, cancellationToken);
            await ApplyPendingAndRestartAsync(cancellationToken);
        }

        private static async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destinationPath);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(100d, readTotal * 100d / total));
                }
                else
                {
                    progress?.Report(-1);
                }
            }

            progress?.Report(100);
        }

        private static async Task VerifySha256Async(string filePath, string expectedHex, CancellationToken cancellationToken)
        {
            var expected = (expectedHex ?? string.Empty).Trim().Replace("-", "", StringComparison.Ordinal);
            if (expected.Length != 64)
            {
                throw new InvalidOperationException("latest.json sha256 is invalid.");
            }

            await using var stream = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var actual = Convert.ToHexString(hash);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Downloaded update failed integrity check (SHA-256 mismatch).");
            }
        }

        private static string BuildHelperScript(int pid, string sourceExe, string destinationExe)
        {
            var src = sourceExe.Replace("\"", "");
            var dest = destinationExe.Replace("\"", "");
            var destDir = Path.GetDirectoryName(dest)?.Replace("\"", "") ?? string.Empty;
            return $@"@echo off
setlocal
set PID={pid}
set SRC={src}
set DEST={dest}
set DESTDIR={destDir}
:wait
tasklist /FI ""PID eq %PID%"" | find ""%PID%"" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
if not exist ""%DESTDIR%"" mkdir ""%DESTDIR%""
copy /Y ""%SRC%"" ""%DEST%"" >nul
if errorlevel 1 (
  ping 127.0.0.1 -n 2 >nul
  copy /Y ""%SRC%"" ""%DEST%"" >nul
)
start """" ""%DEST%""
del ""%~f0"" >nul 2>&1
endlocal
";
        }

        private static string ResolveDownloadUrl(string downloadUrl, string fileName)
        {
            var value = (downloadUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = fileName;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("Download URL must be http or https.");
                }

                return absolute.ToString();
            }

            if (value.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Relative download URL is invalid.");
            }

            return CombineUrl(UpdateOptions.BaseUrl, value);
        }

        private static string CombineUrl(string baseUrl, string relative)
        {
            var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            var path = (relative ?? string.Empty).Trim().TrimStart('/');
            return $"{root}/{path}";
        }

        public static string NormalizeVersionString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "0.0.0";
            }

            var trimmed = value.Trim();
            var plus = trimmed.IndexOf('+');
            if (plus >= 0) trimmed = trimmed[..plus];
            var dash = trimmed.IndexOf('-');
            if (dash >= 0) trimmed = trimmed[..dash];

            var parts = trimmed.Split('.');
            if (parts.Length >= 3)
            {
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            }

            if (parts.Length == 2)
            {
                return $"{parts[0]}.{parts[1]}.0";
            }

            return $"{trimmed}.0.0";
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GitDeployPro-Updater/1.0");
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(45)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GitDeployPro-Updater/1.0");
            return client;
        }
    }
}
