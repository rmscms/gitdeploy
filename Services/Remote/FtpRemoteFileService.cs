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
                    ConnectTimeout = 20000,
                    ReadTimeout = 45000,
                    DataConnectionReadTimeout = 45000,
                    RetryAttempts = 1
                }
            };

            try
            {
                await _client.Connect(cancellationToken).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ProfileId = profile.Id;
            }
            catch (OperationCanceledException)
            {
                Abort();
                throw;
            }
        }

        public void Abort()
        {
            var client = _client;
            _client = null;
            ProfileId = string.Empty;
            if (client == null)
            {
                return;
            }

            try
            {
                client.Dispose();
            }
            catch
            {
                // Ignore abort races with an in-flight Connect().
            }
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
                    ModifiedUtc = ToUtcOrNull(item.Modified),
                    CreatedUtc = ToUtcOrNull(item.Created)
                });
            }

            return entries;
        }

        public Task<string> OpenTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            ReadTextFileAsync(remotePath, cancellationToken);

        public async Task<string> ReadTextFileAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await using var stream = await _client!.OpenRead(remotePath, FtpDataType.Binary, 0, true, cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        public async Task DownloadFileAsync(
            string remotePath,
            string localPath,
            IProgress<RemoteDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            var parentDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            long totalBytes = 0;
            try
            {
                var stat = await GetFileStatAsync(remotePath, cancellationToken);
                totalBytes = Math.Max(0, stat.SizeBytes);
            }
            catch
            {
                // Best-effort size detection.
            }

            progress?.Report(new RemoteDownloadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0
            });

            IProgress<FtpProgress>? ftpProgress = null;
            if (progress != null)
            {
                ftpProgress = new Progress<FtpProgress>(ftp =>
                {
                    if (ftp == null)
                    {
                        return;
                    }

                    var transferred = Math.Max(0, ftp.TransferredBytes);
                    var inferredTotal = totalBytes > 0 ? totalBytes : Math.Max(transferred, 1);
                    var percent = ftp.Progress >= 0
                        ? ftp.Progress
                        : (inferredTotal > 0 ? (double)transferred / inferredTotal * 100d : 0d);
                    progress.Report(new RemoteDownloadProgress
                    {
                        BytesTransferred = transferred,
                        TotalBytes = inferredTotal,
                        Percent = percent,
                        IsIndeterminate = inferredTotal <= 0
                    });
                });
            }

            await _client!.DownloadFile(
                localPath,
                remotePath,
                FtpLocalExists.Overwrite,
                FtpVerify.None,
                progress: ftpProgress,
                token: cancellationToken);

            if (progress != null)
            {
                var finalBytes = totalBytes > 0 && File.Exists(localPath) ? totalBytes : (File.Exists(localPath) ? new FileInfo(localPath).Length : 0);
                progress.Report(new RemoteDownloadProgress
                {
                    BytesTransferred = finalBytes,
                    TotalBytes = finalBytes,
                    Percent = 100,
                    IsIndeterminate = false
                });
            }
        }

        public async Task DownloadDirectoryAsync(
            string remoteDirectoryPath,
            string localDirectoryPath,
            IProgress<RemoteDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            Directory.CreateDirectory(localDirectoryPath);
            var normalizedRemoteRoot = RemotePathResolver.EnsureTrailingSlash(remoteDirectoryPath).TrimEnd('/');

            var fileQueue = new List<(string RemotePath, string LocalPath, long SizeBytes)>();
            await BuildDownloadQueueAsync(normalizedRemoteRoot, localDirectoryPath, fileQueue, cancellationToken);

            var totalBytes = fileQueue.Sum(item => Math.Max(0, item.SizeBytes));
            long transferredBytes = 0;
            progress?.Report(new RemoteDownloadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0
            });

            foreach (var file in fileQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DownloadFileAsync(file.RemotePath, file.LocalPath, null, cancellationToken);
                var actualBytes = File.Exists(file.LocalPath) ? new FileInfo(file.LocalPath).Length : Math.Max(0, file.SizeBytes);
                transferredBytes += actualBytes;
                progress?.Report(new RemoteDownloadProgress
                {
                    BytesTransferred = transferredBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes > 0 ? (double)transferredBytes / totalBytes * 100d : 100d,
                    IsIndeterminate = totalBytes <= 0
                });
            }
        }

        public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await _client!.Rename(sourcePath, destinationPath, cancellationToken);
        }

        public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            if (isDirectory)
            {
                await _client!.DeleteDirectory(remotePath, FtpListOption.Recursive, cancellationToken);
                return;
            }

            await _client!.DeleteFile(remotePath, cancellationToken);
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

            await FtpDirectoryEnsure.EnsureAsync(_client!, remoteDirectoryPath, cancellationToken);
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
                ModifiedUtc = ToUtcOrNull(hit.Modified),
                CreatedUtc = ToUtcOrNull(hit.Created)
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

        private static DateTime? ToUtcOrNull(DateTime value)
        {
            if (value == DateTime.MinValue || value.Year < 1980)
            {
                return null;
            }

            return AppTimeService.NormalizeUtc(value);
        }

        public async Task<RemoteUnixPermissionInfo> GetUnixPermissionsAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            try
            {
                var mode = await _client!.GetChmod(remotePath, cancellationToken);
                return new RemoteUnixPermissionInfo
                {
                    Exists = true,
                    CanReadMode = true,
                    CanChange = true,
                    Mode = UnixPermissionMode.Normalize(mode)
                };
            }
            catch (Exception ex)
            {
                var reason = UnixPermissionMode.ExplainError(ex, usesSsh: false);
                var unsupported = reason.Contains("SITE CHMOD", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("does not support", StringComparison.OrdinalIgnoreCase);
                return new RemoteUnixPermissionInfo
                {
                    Exists = true,
                    CanReadMode = false,
                    CanChange = !unsupported,
                    Mode = 0,
                    Reason = unsupported
                        ? reason
                        : $"Could not read current permissions. You can still try Apply. {reason}"
                };
            }
        }

        public async Task SetUnixPermissionsAsync(string remotePath, int mode, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            try
            {
                var octal = UnixPermissionMode.ToOctal(mode);
                var path = (remotePath ?? string.Empty).Replace("\\", "/");
                var quoted = path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
                var reply = await _client!.Execute($"SITE CHMOD {octal} {quoted}", cancellationToken);
                if (!reply.Success)
                {
                    var detail = string.IsNullOrWhiteSpace(reply.Message) ? reply.Code.ToString() : $"{reply.Code} {reply.Message}".Trim();
                    throw new InvalidOperationException(detail);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(UnixPermissionMode.ExplainError(ex, usesSsh: false), ex);
            }
        }

        private async Task BuildDownloadQueueAsync(
            string remoteDirectoryPath,
            string localDirectoryPath,
            List<(string RemotePath, string LocalPath, long SizeBytes)> queue,
            CancellationToken cancellationToken)
        {
            var listing = await _client!.GetListing(
                RemotePathResolver.EnsureTrailingSlash(remoteDirectoryPath),
                FtpListOption.AllFiles | FtpListOption.Size | FtpListOption.Modify,
                cancellationToken);
            foreach (var item in listing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Type == FtpObjectType.Directory)
                {
                    if (item.Name is "." or "..")
                    {
                        continue;
                    }

                    var localChild = Path.Combine(localDirectoryPath, item.Name);
                    Directory.CreateDirectory(localChild);
                    await BuildDownloadQueueAsync(item.FullName, localChild, queue, cancellationToken);
                    continue;
                }

                if (item.Type == FtpObjectType.File)
                {
                    var localFile = Path.Combine(localDirectoryPath, item.Name);
                    queue.Add((item.FullName, localFile, Math.Max(0, item.Size)));
                }
            }
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
