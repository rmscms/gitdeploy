using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public sealed class FtpRemoteFileService : IRemoteFileService
    {
        private AsyncFtpClient? _client;

        public bool IsConnected => _client?.IsConnected == true;
        public bool UsesSsh => false;
        public string ProfileId { get; private set; } = string.Empty;

        public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.UseSSH)
            {
                throw new InvalidOperationException("FTP remote file service cannot connect to SFTP profiles.");
            }

            await DisconnectAsync();
            var password = EncryptionService.Decrypt(profile.Password);
            _client = new AsyncFtpClient(profile.Host, profile.Username, password, profile.Port <= 0 ? 21 : profile.Port)
            {
                Config =
                {
                    DataConnectionType = profile.PassiveMode ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
                    ReadTimeout = 45000,
                    DataConnectionReadTimeout = 45000,
                    RetryAttempts = 2
                }
            };

            await _client.Connect(cancellationToken);
            ProfileId = profile.Id;
        }

        public async Task DisconnectAsync()
        {
            if (_client == null) return;
            try
            {
                if (_client.IsConnected)
                {
                    await _client.Disconnect();
                }
            }
            catch
            {
                // Ignore disconnect failures.
            }
            finally
            {
                _client.Dispose();
                _client = null;
                ProfileId = string.Empty;
            }
        }

        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var remotePath = RemotePathResolver.EnsureTrailingSlash(path);
            var list = await _client!.GetListing(remotePath, FtpListOption.AllFiles | FtpListOption.Size | FtpListOption.Modify, cancellationToken);
            var entries = new List<RemoteDirectoryEntry>(list.Length);
            foreach (var item in list)
            {
                entries.Add(new RemoteDirectoryEntry
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    SizeBytes = item.Size < 0 ? 0 : item.Size,
                    ModifiedUtc = item.Modified == DateTime.MinValue ? null : AppTimeService.NormalizeUtc(item.Modified)
                });
            }

            return entries;
        }

        public async Task<string> ReadTextFileAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await using var stream = await _client!.OpenRead(remotePath, FtpDataType.Binary, 0, true, cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        public async Task UploadTextFileAsync(
            string remotePath,
            string content,
            IProgress<RemoteUploadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var remoteDirectory = GetDirectoryPath(remotePath);
            if (!string.IsNullOrWhiteSpace(remoteDirectory))
            {
                await EnsureDirectoryAsync(remoteDirectory, cancellationToken);
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"gdp-remote-edit-{Guid.NewGuid():N}.txt");
            var totalBytes = Encoding.UTF8.GetByteCount(content ?? string.Empty);
            progress?.Report(new RemoteUploadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0
            });

            try
            {
                await File.WriteAllTextAsync(tempFile, content ?? string.Empty, new UTF8Encoding(false), cancellationToken);
                IProgress<FtpProgress>? ftpProgressReporter = null;
                if (progress != null)
                {
                    ftpProgressReporter = new Progress<FtpProgress>(ftpProgress =>
                    {
                        if (ftpProgress == null)
                        {
                            return;
                        }

                        var bytesTransferred = ftpProgress.TransferredBytes;
                        var bytesTotal = totalBytes > 0 ? totalBytes : Math.Max(bytesTransferred, 1);
                        var percent = ftpProgress.Progress >= 0
                            ? ftpProgress.Progress
                            : (bytesTotal > 0 ? (double)bytesTransferred / bytesTotal * 100d : 0d);
                        progress.Report(new RemoteUploadProgress
                        {
                            BytesTransferred = bytesTransferred,
                            TotalBytes = bytesTotal,
                            Percent = percent,
                            IsIndeterminate = ftpProgress.Progress < 0 && bytesTotal <= 0
                        });
                    });
                }

                await _client!.UploadFile(
                    tempFile,
                    remotePath,
                    FtpRemoteExists.Overwrite,
                    true,
                    FtpVerify.None,
                    progress: ftpProgressReporter,
                    token: cancellationToken);

                progress?.Report(new RemoteUploadProgress
                {
                    BytesTransferred = totalBytes,
                    TotalBytes = totalBytes,
                    Percent = 100,
                    IsIndeterminate = false
                });
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }
        }

        public async Task EnsureDirectoryAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            if (string.IsNullOrWhiteSpace(remoteDirectoryPath))
            {
                return;
            }

            var normalized = RemotePathResolver.EnsureTrailingSlash(remoteDirectoryPath);
            if (!await _client!.DirectoryExists(normalized, cancellationToken))
            {
                await _client.CreateDirectory(normalized, cancellationToken);
            }
        }

        public async Task<RemoteFileStat> GetFileStatAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return new RemoteFileStat { FullPath = string.Empty, Exists = false };
            }

            var parent = GetDirectoryPath(remotePath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                parent = "/";
            }

            var fileName = Path.GetFileName(remotePath.Replace("\\", "/"));
            var listing = await _client!.GetListing(
                RemotePathResolver.EnsureTrailingSlash(parent),
                FtpListOption.AllFiles | FtpListOption.Size | FtpListOption.Modify,
                cancellationToken);
            var hit = listing.FirstOrDefault(item =>
                string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.FullName.TrimEnd('/'), remotePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

            if (hit == null)
            {
                return new RemoteFileStat
                {
                    FullPath = remotePath,
                    Exists = false
                };
            }

            return new RemoteFileStat
            {
                FullPath = hit.FullName,
                Exists = true,
                IsDirectory = hit.Type == FtpObjectType.Directory,
                SizeBytes = hit.Size < 0 ? 0 : hit.Size,
                ModifiedUtc = hit.Modified == DateTime.MinValue ? null : AppTimeService.NormalizeUtc(hit.Modified)
            };
        }

        private static string GetDirectoryPath(string remotePath)
        {
            var normalized = remotePath.Replace("\\", "/");
            var idx = normalized.LastIndexOf('/', normalized.Length - 1);
            if (idx <= 0)
            {
                return "/";
            }

            return normalized[..idx];
        }

        private void EnsureConnected()
        {
            if (_client == null || !_client.IsConnected)
            {
                throw new InvalidOperationException("FTP client is not connected.");
            }
        }
    }
}
