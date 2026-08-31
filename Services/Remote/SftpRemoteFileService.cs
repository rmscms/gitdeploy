using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GitDeployPro.Services.Remote
{
    public sealed class SftpRemoteFileService : IRemoteFileService
    {
        private SftpClient? _client;

        public bool IsConnected => _client?.IsConnected == true;
        public bool UsesSsh => true;
        public string ProfileId { get; private set; } = string.Empty;

        public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!profile.UseSSH)
            {
                throw new InvalidOperationException("SFTP remote file service requires SSH-enabled profile.");
            }

            await DisconnectAsync();
            SftpClient? client = null;
            using var cancelRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    client?.Dispose();
                }
                catch
                {
                    // Swallow dispose races while aborting a hung Connect().
                }
            });

            var connectTask = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var methods = new List<AuthenticationMethod>();
                var password = EncryptionService.Decrypt(profile.Password);
                if (!string.IsNullOrWhiteSpace(password))
                {
                    methods.Add(new PasswordAuthenticationMethod(profile.Username, password));
                }

                if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath) && File.Exists(profile.PrivateKeyPath))
                {
                    PrivateKeyFile keyFile;
                    try
                    {
                        keyFile = !string.IsNullOrWhiteSpace(password)
                            ? new PrivateKeyFile(profile.PrivateKeyPath, password)
                            : new PrivateKeyFile(profile.PrivateKeyPath);
                    }
                    catch (SshPassPhraseNullOrEmptyException)
                    {
                        keyFile = new PrivateKeyFile(profile.PrivateKeyPath);
                    }
                    catch (SshException)
                    {
                        keyFile = new PrivateKeyFile(profile.PrivateKeyPath);
                    }

                    methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
                }

                if (methods.Count == 0)
                {
                    throw new InvalidOperationException("Provide SFTP password or private key.");
                }

                var info = new ConnectionInfo(
                    profile.Host,
                    profile.Port <= 0 ? 22 : profile.Port,
                    profile.Username,
                    methods.ToArray());
                info.Timeout = TimeSpan.FromSeconds(45);
                client = new SftpClient(info);
                client.KeepAliveInterval = TimeSpan.FromSeconds(15);
                client.OperationTimeout = TimeSpan.FromMilliseconds(TransferTimeoutPolicy.IdleStallMs);
                cancellationToken.ThrowIfCancellationRequested();
                client.Connect();
                cancellationToken.ThrowIfCancellationRequested();
                _client = client;
                ProfileId = profile.Id;
            });

            try
            {
                await connectTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Abort();
                try
                {
                    client?.Dispose();
                }
                catch
                {
                    // Ignore.
                }

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

        public Task DisconnectAsync()
        {
            if (_client == null) return Task.CompletedTask;
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
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

            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var entries = await Task.Run(() =>
            {
                var list = _client!.ListDirectory(path)
                    .Where(x => x.Name != "." && x.Name != "..")
                    .ToList();
                var mapped = new List<RemoteDirectoryEntry>(list.Count);
                foreach (var item in list)
                {
                    mapped.Add(new RemoteDirectoryEntry
                    {
                        Name = item.Name,
                        FullPath = item.FullName,
                        IsDirectory = item.IsDirectory,
                        SizeBytes = item.Attributes?.Size ?? 0,
                        ModifiedUtc = AppTimeService.NormalizeUtc(item.LastWriteTimeUtc),
                        CreatedUtc = null
                    });
                }

                return (IReadOnlyList<RemoteDirectoryEntry>)mapped;
            }).WaitAsync(cancellationToken);
            return entries;
        }

        public Task<string> OpenTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            ReadTextFileAsync(remotePath, cancellationToken);

        public async Task<string> ReadTextFileAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            using var memory = new MemoryStream();
            await Task.Run(() =>
            {
                using var stream = _client!.OpenRead(remotePath);
                stream.CopyTo(memory);
            }, cancellationToken);
            return Encoding.UTF8.GetString(memory.ToArray());
        }

        public async Task DownloadFileAsync(
            string remotePath,
            string localPath,
            IProgress<RemoteDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
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

            var currentFileName = Path.GetFileName(remotePath);
            ApplyIdleTimeout();
            progress?.Report(new RemoteDownloadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0,
                CurrentFileName = currentFileName
            });

            using var stall = new TransferStallWatchdog(cancellationToken, TransferTimeoutPolicy.IdleStallMsFor(totalBytes));
            try
            {
                await Task.Run(async () =>
                {
                    using var remoteStream = _client!.OpenRead(remotePath);
                    await using var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                    var buffer = new byte[64 * 1024];
                    long transferred = 0;
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length, stall.Token)) > 0)
                    {
                        await localStream.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                        transferred += read;
                        stall.Pulse(transferred);
                        progress?.Report(new RemoteDownloadProgress
                        {
                            BytesTransferred = transferred,
                            TotalBytes = totalBytes > 0 ? totalBytes : transferred,
                            Percent = totalBytes > 0 ? (double)transferred / totalBytes * 100d : 0d,
                            IsIndeterminate = totalBytes <= 0,
                            CurrentFileName = currentFileName
                        });
                    }
                }, stall.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(TransferTimeoutPolicy.StalledMessage(totalBytes));
            }

            if (progress != null)
            {
                var finalBytes = File.Exists(localPath) ? new FileInfo(localPath).Length : totalBytes;
                progress.Report(new RemoteDownloadProgress
                {
                    BytesTransferred = finalBytes,
                    TotalBytes = finalBytes,
                    Percent = 100,
                    IsIndeterminate = false,
                    CurrentFileName = currentFileName
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

            var queue = new List<(string RemotePath, string LocalPath, long SizeBytes)>();
            await Task.Run(() => BuildDownloadQueue(remoteDirectoryPath, localDirectoryPath, queue, cancellationToken), cancellationToken);

            var totalBytes = queue.Sum(item => Math.Max(0, item.SizeBytes));
            long transferredBytes = 0;
            progress?.Report(new RemoteDownloadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0,
                CurrentFileName = queue.Count > 0 ? Path.GetFileName(queue[0].RemotePath) : string.Empty
            });

            foreach (var file in queue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file.RemotePath);
                var fileBase = transferredBytes;
                IProgress<RemoteDownloadProgress>? fileProgress = progress == null
                    ? null
                    : new Progress<RemoteDownloadProgress>(p =>
                    {
                        var combined = fileBase + Math.Max(0, p.BytesTransferred);
                        progress.Report(new RemoteDownloadProgress
                        {
                            BytesTransferred = combined,
                            TotalBytes = totalBytes,
                            Percent = totalBytes > 0 ? (double)combined / totalBytes * 100d : p.Percent,
                            IsIndeterminate = totalBytes <= 0,
                            CurrentFileName = fileName
                        });
                    });
                await DownloadFileAsync(file.RemotePath, file.LocalPath, fileProgress, cancellationToken);
                var actualBytes = File.Exists(file.LocalPath) ? new FileInfo(file.LocalPath).Length : Math.Max(0, file.SizeBytes);
                transferredBytes += actualBytes;
                progress?.Report(new RemoteDownloadProgress
                {
                    BytesTransferred = transferredBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes > 0 ? (double)transferredBytes / totalBytes * 100d : 100d,
                    IsIndeterminate = totalBytes <= 0,
                    CurrentFileName = fileName
                });
            }
        }

        public async Task<IReadOnlyList<RemoteTransferJob>> PlanDownloadDirectoryAsync(
            string remoteDirectoryPath,
            string localDirectoryPath,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            Directory.CreateDirectory(localDirectoryPath);
            var queue = new List<(string RemotePath, string LocalPath, long SizeBytes)>();
            await Task.Run(() => BuildDownloadQueue(remoteDirectoryPath, localDirectoryPath, queue, cancellationToken), cancellationToken);
            return queue.Select(item => new RemoteTransferJob
            {
                RemotePath = item.RemotePath,
                LocalPath = item.LocalPath,
                SizeBytes = item.SizeBytes
            }).ToList();
        }

        public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await Task.Run(() => _client!.RenameFile(sourcePath, destinationPath), cancellationToken);
        }

        public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await Task.Run(() =>
            {
                if (isDirectory)
                {
                    DeleteDirectoryRecursive(remotePath);
                    _client!.DeleteDirectory(remotePath);
                    return;
                }

                _client!.DeleteFile(remotePath);
            }, cancellationToken);
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

            var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            progress?.Report(new RemoteUploadProgress
            {
                BytesTransferred = 0,
                TotalBytes = bytes.LongLength,
                Percent = 0,
                IsIndeterminate = bytes.LongLength <= 0
            });

            await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                _client!.UploadFile(stream, remotePath, true, uploaded =>
                {
                    if (progress == null)
                    {
                        return;
                    }

                    var total = bytes.LongLength;
                    var transferred = (long)uploaded;
                    var percent = total > 0 ? (double)transferred / total * 100d : 0d;
                    if (percent > 100d)
                    {
                        percent = 100d;
                    }

                    progress.Report(new RemoteUploadProgress
                    {
                        BytesTransferred = transferred,
                        TotalBytes = total,
                        Percent = percent,
                        IsIndeterminate = false
                    });
                });
            }, cancellationToken);

            progress?.Report(new RemoteUploadProgress
            {
                BytesTransferred = bytes.LongLength,
                TotalBytes = bytes.LongLength,
                Percent = 100,
                IsIndeterminate = false
            });
        }

        public async Task UploadLocalFileAsync(
            string localPath,
            string remotePath,
            IProgress<RemoteUploadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                throw new FileNotFoundException("Local file was not found.", localPath);
            }

            await PrepareRemoteFileUploadAsync(remotePath, cancellationToken);

            var totalBytes = new FileInfo(localPath).Length;
            ApplyFileTimeout(totalBytes);
            progress?.Report(new RemoteUploadProgress
            {
                BytesTransferred = 0,
                TotalBytes = totalBytes,
                Percent = 0,
                IsIndeterminate = totalBytes <= 0
            });

            using var stall = new TransferStallWatchdog(cancellationToken, TransferTimeoutPolicy.IdleStallMsFor(totalBytes));
            try
            {
                await Task.Run(() =>
                {
                    using var stream = File.OpenRead(localPath);
                    _client!.UploadFile(stream, remotePath, true, uploaded =>
                    {
                        var transferred = (long)uploaded;
                        stall.Pulse(transferred);
                        if (progress == null)
                        {
                            return;
                        }

                        var percent = totalBytes > 0 ? (double)transferred / totalBytes * 100d : 0d;
                        if (percent > 100d)
                        {
                            percent = 100d;
                        }

                        progress.Report(new RemoteUploadProgress
                        {
                            BytesTransferred = transferred,
                            TotalBytes = totalBytes,
                            Percent = percent,
                            IsIndeterminate = false
                        });
                    });
                    stall.Token.ThrowIfCancellationRequested();
                }, stall.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(TransferTimeoutPolicy.StalledMessage(totalBytes));
            }

            progress?.Report(new RemoteUploadProgress
            {
                BytesTransferred = totalBytes,
                TotalBytes = totalBytes,
                Percent = 100,
                IsIndeterminate = false
            });
        }

        public async Task EnsureDirectoryAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var normalized = RemotePathResolver.NormalizeRemoteBase(remoteDirectoryPath).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
            {
                return;
            }

            await Task.Run(() =>
            {
                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var current = "/";
                foreach (var segment in segments)
                {
                    current = current.TrimEnd('/') + "/" + segment;
                    if (!_client!.Exists(current))
                    {
                        _client.CreateDirectory(current);
                    }
                }
            }, cancellationToken);
        }

        private async Task PrepareRemoteFileUploadAsync(string remotePath, CancellationToken cancellationToken)
        {
            EnsureConnected();
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return;
            }

            await Task.Run(() =>
            {
                if (_client!.Exists(remotePath) && _client.GetAttributes(remotePath).IsDirectory)
                {
                    DeleteDirectoryRecursive(remotePath);
                    _client.DeleteDirectory(remotePath);
                }
            }, cancellationToken);

            var parent = GetDirectoryPath(remotePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                await EnsureDirectoryAsync(parent, cancellationToken);
            }
        }

        public async Task<RemoteFileStat> GetFileStatAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            return await Task.Run(() =>
            {
                if (!_client!.Exists(remotePath))
                {
                    return new RemoteFileStat
                    {
                        FullPath = remotePath,
                        Exists = false
                    };
                }

                var attributes = _client.GetAttributes(remotePath);
                return new RemoteFileStat
                {
                    FullPath = remotePath,
                    Exists = true,
                    IsDirectory = attributes.IsDirectory,
                    SizeBytes = attributes.Size,
                    ModifiedUtc = AppTimeService.NormalizeUtc(attributes.LastWriteTimeUtc),
                    CreatedUtc = null
                };
            }, cancellationToken);
        }

        public async Task<RemoteUnixPermissionInfo> GetUnixPermissionsAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            return await Task.Run(() =>
            {
                if (!_client!.Exists(remotePath))
                {
                    return new RemoteUnixPermissionInfo
                    {
                        Exists = false,
                        CanReadMode = false,
                        CanChange = false,
                        Reason = "This file or folder was not found on the server."
                    };
                }

                var attributes = _client.GetAttributes(remotePath);
                return new RemoteUnixPermissionInfo
                {
                    Exists = true,
                    CanReadMode = true,
                    CanChange = true,
                    Mode = ModeFromAttributes(attributes)
                };
            }, cancellationToken);
        }

        public async Task SetUnixPermissionsAsync(string remotePath, int mode, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var normalized = UnixPermissionMode.Normalize(mode);
            try
            {
                await Task.Run(() =>
                {
                    _client!.ChangePermissions(remotePath, (short)normalized);
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(UnixPermissionMode.ExplainError(ex, usesSsh: true), ex);
            }
        }

        private static int ModeFromAttributes(Renci.SshNet.Sftp.SftpFileAttributes attributes)
        {
            var mode = 0;
            if (attributes.OwnerCanRead) mode |= UnixPermissionMode.OwnerRead;
            if (attributes.OwnerCanWrite) mode |= UnixPermissionMode.OwnerWrite;
            if (attributes.OwnerCanExecute) mode |= UnixPermissionMode.OwnerExecute;
            if (attributes.GroupCanRead) mode |= UnixPermissionMode.GroupRead;
            if (attributes.GroupCanWrite) mode |= UnixPermissionMode.GroupWrite;
            if (attributes.GroupCanExecute) mode |= UnixPermissionMode.GroupExecute;
            if (attributes.OthersCanRead) mode |= UnixPermissionMode.OthersRead;
            if (attributes.OthersCanWrite) mode |= UnixPermissionMode.OthersWrite;
            if (attributes.OthersCanExecute) mode |= UnixPermissionMode.OthersExecute;
            return mode;
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

        private void BuildDownloadQueue(
            string remoteDirectoryPath,
            string localDirectoryPath,
            List<(string RemotePath, string LocalPath, long SizeBytes)> queue,
            CancellationToken cancellationToken)
        {
            foreach (var entry in _client!.ListDirectory(remoteDirectoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    var localChild = Path.Combine(localDirectoryPath, entry.Name);
                    Directory.CreateDirectory(localChild);
                    BuildDownloadQueue(entry.FullName, localChild, queue, cancellationToken);
                    continue;
                }

                var localFile = Path.Combine(localDirectoryPath, entry.Name);
                queue.Add((entry.FullName, localFile, Math.Max(0, entry.Attributes?.Size ?? 0)));
            }
        }

        private void DeleteDirectoryRecursive(string remoteDirectoryPath)
        {
            foreach (var entry in _client!.ListDirectory(remoteDirectoryPath))
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    DeleteDirectoryRecursive(entry.FullName);
                    _client.DeleteDirectory(entry.FullName);
                    continue;
                }

                _client.DeleteFile(entry.FullName);
            }
        }

        private void ApplyIdleTimeout()
        {
            if (_client == null)
            {
                return;
            }

            _client.OperationTimeout = TimeSpan.FromMilliseconds(TransferTimeoutPolicy.IdleStallMs);
        }

        private void ApplyFileTimeout(long sizeBytes)
        {
            if (_client == null)
            {
                return;
            }

            _client.OperationTimeout = TransferTimeoutPolicy.ForFileBytes(sizeBytes);
        }

        private void EnsureConnected()
        {
            if (_client == null || !_client.IsConnected)
            {
                throw new InvalidOperationException("SFTP client is not connected.");
            }
        }
    }
}
