using System;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;

namespace GitDeployPro.Services.Remote
{
    /// <summary>
    /// Creates remote FTP directories segment-by-segment.
    /// Avoids Windows <see cref="System.IO.Path.GetDirectoryName"/> on Unix-style remote paths,
    /// which can produce wrong parents and leave nested new folders missing on the server.
    /// </summary>
    public static class FtpDirectoryEnsure
    {
        public static string GetParentDirectory(string remoteFileOrDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(remoteFileOrDirectoryPath))
            {
                return "/";
            }

            var normalized = remoteFileOrDirectoryPath.Replace('\\', '/').TrimEnd('/');
            var idx = normalized.LastIndexOf('/');
            if (idx <= 0)
            {
                return "/";
            }

            return normalized[..idx];
        }

        public static async Task EnsureAsync(
            AsyncFtpClient client,
            string remoteDirectoryPath,
            CancellationToken cancellationToken = default)
        {
            if (client == null || string.IsNullOrWhiteSpace(remoteDirectoryPath))
            {
                return;
            }

            var normalized = remoteDirectoryPath.Replace('\\', '/').Trim();
            if (!normalized.StartsWith('/'))
            {
                normalized = "/" + normalized;
            }

            normalized = normalized.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
            {
                return;
            }

            // Fast path: already exists.
            try
            {
                if (await client.DirectoryExists(normalized, cancellationToken))
                {
                    return;
                }
            }
            catch
            {
                // Fall through to segment creation.
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current += "/" + segment;

                try
                {
                    if (await client.DirectoryExists(current, cancellationToken))
                    {
                        continue;
                    }
                }
                catch
                {
                    // Try create anyway.
                }

                try
                {
                    await client.CreateDirectory(current, cancellationToken);
                }
                catch (Exception)
                {
                    // Another client may have created it; verify before failing.
                    if (!await client.DirectoryExists(current, cancellationToken))
                    {
                        throw;
                    }
                }
            }
        }

        public static Task EnsureParentOfFileAsync(
            AsyncFtpClient client,
            string remoteFilePath,
            CancellationToken cancellationToken = default)
        {
            var parent = GetParentDirectory(remoteFilePath);
            return EnsureAsync(client, parent, cancellationToken);
        }
    }
}
