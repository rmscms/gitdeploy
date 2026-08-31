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

        /// <summary>Drop the socket immediately. Do not wait for Connect/list timeouts.</summary>
        void Abort();

        Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
        Task<string> OpenTextAsync(string remotePath, CancellationToken cancellationToken = default);
        Task<string> ReadTextFileAsync(string remotePath, CancellationToken cancellationToken = default);
        Task DownloadFileAsync(
            string remotePath,
            string localPath,
            IProgress<RemoteDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default);
        Task DownloadDirectoryAsync(
            string remoteDirectoryPath,
            string localDirectoryPath,
            IProgress<RemoteDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RemoteTransferJob>> PlanDownloadDirectoryAsync(
            string remoteDirectoryPath,
            string localDirectoryPath,
            CancellationToken cancellationToken = default);
        Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
        Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken = default);
        Task UploadTextFileAsync(
            string remotePath,
            string content,
            IProgress<RemoteUploadProgress>? progress = null,
            CancellationToken cancellationToken = default);
        Task UploadLocalFileAsync(
            string localPath,
            string remotePath,
            IProgress<RemoteUploadProgress>? progress = null,
            CancellationToken cancellationToken = default);
        Task EnsureDirectoryAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default);
        Task<RemoteFileStat> GetFileStatAsync(string remotePath, CancellationToken cancellationToken = default);
        Task<RemoteUnixPermissionInfo> GetUnixPermissionsAsync(string remotePath, CancellationToken cancellationToken = default);
        Task SetUnixPermissionsAsync(string remotePath, int mode, CancellationToken cancellationToken = default);
    }
}
