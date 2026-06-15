using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public interface IRemoteFileService
    {
        bool IsConnected { get; }
        bool UsesSsh { get; }
        string ProfileId { get; }

        Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
        Task DisconnectAsync();

        Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
        Task<string> ReadTextFileAsync(string remotePath, CancellationToken cancellationToken = default);
        Task UploadTextFileAsync(
            string remotePath,
            string content,
            IProgress<RemoteUploadProgress>? progress = null,
            CancellationToken cancellationToken = default);
        Task EnsureDirectoryAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default);
        Task<RemoteFileStat> GetFileStatAsync(string remotePath, CancellationToken cancellationToken = default);
    }
}
