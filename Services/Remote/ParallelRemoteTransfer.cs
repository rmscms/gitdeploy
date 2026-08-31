using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public sealed class ParallelTransferResult
    {
        public int WorkerCount { get; init; }
        public int Completed { get; init; }
        public int Skipped { get; init; }
        public IReadOnlyList<RemoteTransferJob> Missing { get; init; } = Array.Empty<RemoteTransferJob>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public bool IsComplete => Missing.Count == 0 && Errors.Count == 0;
    }

    public static class ParallelRemoteTransfer
    {
        public static Task<ParallelTransferResult> DownloadAsync(
            ConnectionProfile profile,
            IReadOnlyList<RemoteTransferJob> jobs,
            int workers,
            IProgress<RemoteDownloadProgress>? progress,
            CancellationToken cancellationToken,
            IProgress<ParallelTransferProgress>? detailed = null)
        {
            return RunAsync(
                profile,
                jobs,
                workers,
                download: true,
                progress: progress,
                uploadProgress: null,
                skipIfExists: false,
                onJobDone: null,
                detailed,
                cancellationToken);
        }

        public static Task<ParallelTransferResult> UploadAsync(
            ConnectionProfile profile,
            IReadOnlyList<RemoteTransferJob> jobs,
            int workers,
            IProgress<RemoteUploadProgress>? progress,
            CancellationToken cancellationToken,
            bool skipIfExists = false,
            Action<RemoteTransferJob, bool, string?>? onJobDone = null,
            IProgress<ParallelTransferProgress>? detailed = null)
        {
            return RunAsync(
                profile,
                jobs,
                workers,
                download: false,
                progress: null,
                uploadProgress: progress,
                skipIfExists: skipIfExists,
                onJobDone: onJobDone,
                detailed,
                cancellationToken);
        }

        private static async Task<ParallelTransferResult> RunAsync(
            ConnectionProfile profile,
            IReadOnlyList<RemoteTransferJob> jobs,
            int workers,
            bool download,
            IProgress<RemoteDownloadProgress>? progress,
            IProgress<RemoteUploadProgress>? uploadProgress,
            bool skipIfExists,
            Action<RemoteTransferJob, bool, string?>? onJobDone,
            IProgress<ParallelTransferProgress>? detailed,
            CancellationToken cancellationToken)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var list = (jobs ?? Array.Empty<RemoteTransferJob>())
                .Where(job => job != null && !string.IsNullOrWhiteSpace(job.RemotePath) && !string.IsNullOrWhiteSpace(job.LocalPath))
                .ToList();
            if (list.Count == 0)
            {
                return new ParallelTransferResult { WorkerCount = 0, Completed = 0 };
            }

            workers = Math.Clamp(workers, 1, Math.Min(TransferWorkerPrompt.MaxWorkers, list.Count));
            var queue = new ConcurrentQueue<RemoteTransferJob>(
                list.OrderByDescending(job => job.SizeBytes));
            var errors = new ConcurrentBag<string>();
            var snapshots = new ConcurrentDictionary<int, ParallelWorkerSnapshot>();
            var filesDoneByWorker = new int[workers];
            var completed = 0;
            var skipped = 0;
            var transferredBytes = 0L;
            var sequence = 0L;
            var totalBytes = list.Sum(job => Math.Max(0, job.SizeBytes));
            var verb = download ? "download" : "upload";

            ParallelTransferProgress Build(string phase, string lastLine)
            {
                var slots = Enumerable.Range(0, workers)
                    .Select(id => snapshots.TryGetValue(id, out var snap)
                        ? snap
                        : new ParallelWorkerSnapshot { Id = id + 1, State = "Waiting" })
                    .ToList();
                var active = slots.Count(slot =>
                    slot.State is "Connecting" or "Busy");
                var done = Volatile.Read(ref completed);
                var bytes = Interlocked.Read(ref transferredBytes);
                var percent = totalBytes > 0
                    ? (double)bytes / totalBytes * 100d
                    : (list.Count == 0 ? 100d : done * 100d / list.Count);
                return new ParallelTransferProgress
                {
                    Phase = phase,
                    RequestedWorkers = workers,
                    ActiveWorkers = active,
                    Completed = done,
                    Total = list.Count,
                    Skipped = Volatile.Read(ref skipped),
                    ErrorCount = errors.Count,
                    BytesTransferred = bytes,
                    TotalBytes = totalBytes,
                    Percent = percent,
                    IsIndeterminate = totalBytes <= 0 && done == 0,
                    Headline = $"{active}/{workers} workers live · {done}/{list.Count} files · {phase}",
                    LastLine = lastLine,
                    Sequence = Interlocked.Increment(ref sequence),
                    Workers = slots
                };
            }

            void Publish(string phase, string lastLine)
            {
                var detail = Build(phase, lastLine);
                if (download)
                {
                    progress?.Report(new RemoteDownloadProgress
                    {
                        BytesTransferred = detail.BytesTransferred,
                        TotalBytes = detail.TotalBytes,
                        Percent = detail.Percent,
                        IsIndeterminate = detail.IsIndeterminate,
                        CurrentFileName = detail.Headline,
                        Detail = detail
                    });
                }
                else
                {
                    uploadProgress?.Report(new RemoteUploadProgress
                    {
                        BytesTransferred = detail.BytesTransferred,
                        TotalBytes = detail.TotalBytes,
                        Percent = detail.Percent,
                        IsIndeterminate = detail.IsIndeterminate,
                        CurrentFileName = detail.Headline,
                        Detail = detail
                    });
                }

                detailed?.Report(detail);
            }

            void SetWorker(int workerId, string state, string fileName, string detail)
            {
                snapshots[workerId] = new ParallelWorkerSnapshot
                {
                    Id = workerId + 1,
                    State = state,
                    FileName = fileName,
                    Detail = detail,
                    FilesDone = filesDoneByWorker[workerId]
                };
            }

            for (var i = 0; i < workers; i++)
            {
                SetWorker(i, "Waiting", "queued", string.Empty);
            }

            Publish("Starting workers", $"Requested {workers} workers for {list.Count} files");

            async Task WorkerAsync(int workerId)
            {
                var service = RemoteFileServiceFactory.Create(profile);
                SetWorker(workerId, "Connecting", "opening socket…", string.Empty);
                Publish("Connecting", $"W{workerId + 1} connecting");
                try
                {
                    await service.ConnectAsync(profile, cancellationToken);
                    SetWorker(workerId, "Idle", "waiting for a file", "connected");
                    Publish("Transferring", $"W{workerId + 1} connected");

                    while (queue.TryDequeue(out var job))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SetWorker(workerId, "Busy", job.DisplayName, job.RemotePath);
                        Publish("Transferring", $"W{workerId + 1} {verb} {job.DisplayName}");
                        try
                        {
                            if (download)
                            {
                                var localDir = Path.GetDirectoryName(job.LocalPath);
                                if (!string.IsNullOrWhiteSpace(localDir))
                                {
                                    Directory.CreateDirectory(localDir);
                                }

                                await service.DownloadFileAsync(job.RemotePath, job.LocalPath, null, cancellationToken);
                            }
                            else
                            {
                                if (skipIfExists)
                                {
                                    var stat = await service.GetFileStatAsync(job.RemotePath, cancellationToken);
                                    if (stat.Exists && !stat.IsDirectory)
                                    {
                                        Interlocked.Increment(ref skipped);
                                        Interlocked.Increment(ref completed);
                                        Interlocked.Add(ref transferredBytes, Math.Max(0, job.SizeBytes));
                                        filesDoneByWorker[workerId]++;
                                        onJobDone?.Invoke(job, true, null);
                                        SetWorker(workerId, "Idle", $"skipped {job.DisplayName}", "exists");
                                        Publish("Transferring", $"W{workerId + 1} skipped {job.DisplayName}");
                                        continue;
                                    }
                                }

                                await service.UploadLocalFileAsync(job.LocalPath, job.RemotePath, null, cancellationToken);
                            }

                            Interlocked.Add(ref transferredBytes, Math.Max(0, job.SizeBytes));
                            Interlocked.Increment(ref completed);
                            filesDoneByWorker[workerId]++;
                            onJobDone?.Invoke(job, true, null);
                            SetWorker(workerId, "Idle", $"done {job.DisplayName}", $"{filesDoneByWorker[workerId]} files");
                            Publish("Transferring", $"W{workerId + 1} finished {job.DisplayName}");
                        }
                        catch (OperationCanceledException)
                        {
                            SetWorker(workerId, "Cancelled", job.DisplayName, string.Empty);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{job.DisplayName}: {ex.Message}");
                            onJobDone?.Invoke(job, false, ex.Message);
                            SetWorker(workerId, "Error", job.DisplayName, ex.Message);
                            Publish("Transferring", $"W{workerId + 1} failed {job.DisplayName}: {ex.Message}");
                        }
                    }

                    SetWorker(workerId, "Done", $"{filesDoneByWorker[workerId]} files", "queue empty");
                    Publish("Transferring", $"W{workerId + 1} done");
                }
                finally
                {
                    try
                    {
                        await service.DisconnectAsync();
                    }
                    catch
                    {
                        service.Abort();
                    }
                }
            }

            var tasks = Enumerable.Range(0, workers)
                .Select(WorkerAsync)
                .ToArray();
            await Task.WhenAll(tasks);

            List<RemoteTransferJob> missing;
            if (download)
            {
                Publish("Verifying", "Checking local files…");
                missing = list.Where(job => !File.Exists(job.LocalPath)).ToList();
            }
            else
            {
                Publish("Verifying", "Checking remote files…");
                missing = await VerifyRemoteFilesAsync(profile, list, cancellationToken);
            }

            var result = new ParallelTransferResult
            {
                WorkerCount = workers,
                Completed = Volatile.Read(ref completed),
                Skipped = Volatile.Read(ref skipped),
                Missing = missing,
                Errors = errors.ToList()
            };
            Publish(
                "Finished",
                result.IsComplete
                    ? $"All {result.Completed} files verified with {result.WorkerCount} workers"
                    : $"Finished with gaps: {result.Completed}/{list.Count}, missing {result.Missing.Count}, errors {result.Errors.Count}");
            return result;
        }

        private static async Task<List<RemoteTransferJob>> VerifyRemoteFilesAsync(
            ConnectionProfile profile,
            IReadOnlyList<RemoteTransferJob> jobs,
            CancellationToken cancellationToken)
        {
            var missing = new List<RemoteTransferJob>();
            var service = RemoteFileServiceFactory.Create(profile);
            try
            {
                await service.ConnectAsync(profile, cancellationToken);
                foreach (var job in jobs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stat = await service.GetFileStatAsync(job.RemotePath, cancellationToken);
                    if (!stat.Exists || stat.IsDirectory)
                    {
                        missing.Add(job);
                    }
                }
            }
            finally
            {
                try
                {
                    await service.DisconnectAsync();
                }
                catch
                {
                    service.Abort();
                }
            }

            return missing;
        }
    }
}
