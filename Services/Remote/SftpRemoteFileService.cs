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
            await Task.Run(() =>
            {
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
                _client = new SftpClient(info);
                _client.KeepAliveInterval = TimeSpan.FromSeconds(15);
                _client.OperationTimeout = TimeSpan.FromSeconds(45);
                _client.Connect();
                ProfileId = profile.Id;
            }, cancellationToken);
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
                        ModifiedUtc = AppTimeService.NormalizeUtc(item.LastWriteTimeUtc)
                    });
                }

                return (IReadOnlyList<RemoteDirectoryEntry>)mapped;
            }, cancellationToken);
            return entries;
        }

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
                    ModifiedUtc = AppTimeService.NormalizeUtc(attributes.LastWriteTimeUtc)
                };
            }, cancellationToken);
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
                throw new InvalidOperationException("SFTP client is not connected.");
            }
        }
    }
}
