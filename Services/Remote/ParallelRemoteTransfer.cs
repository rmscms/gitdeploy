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
            var smallJobs = list
                .Where(job => job.SizeBytes < TransferTimeoutPolicy.LargeFileBytes)
                .OrderByDescending(job => job.SizeBytes)
                .ToList();
            var largeJobs = list
                .Where(job => job.SizeBytes >= TransferTimeoutPolicy.LargeFileBytes)
                .OrderByDescending(job => job.SizeBytes)
                .ToList();
            var queue = new ConcurrentQueue<RemoteTransferJob>();
            var jobErrors = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var succeeded = new ConcurrentBag<RemoteTransferJob>();
            var snapshots = new ConcurrentDictionary<int, ParallelWorkerSnapshot>();
            var filesDoneByWorker = new int[workers];
            var workerInflightBytes = new long[workers];
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
                long inflight = 0;
                for (var i = 0; i < workers; i++)
                {
                    inflight += Volatile.Read(ref workerInflightBytes[i]);
                }

                var bytes = Interlocked.Read(ref transferredBytes) + inflight;
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
                    ErrorCount = jobErrors.Count,
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

            Publish(
                "Starting workers",
                largeJobs.Count == 0
                    ? $"Requested {workers} workers for {list.Count} files"
                    : $"{smallJobs.Count} small with {workers} workers, then {largeJobs.Count} large");

            IProgress<RemoteUploadProgress> CreateUploadJobProgress(int workerId, RemoteTransferJob job) =>
                new Progress<RemoteUploadProgress>(p =>
                {
                    Volatile.Write(ref workerInflightBytes[workerId], Math.Max(0, p.BytesTransferred));
                    var filePct = p.TotalBytes > 0 ? p.Percent : 0d;
                    SetWorker(workerId, "Busy", job.DisplayName, $"{filePct:0}%");
                    Publish("Transferring", $"W{workerId + 1} {verb} {job.DisplayName} · {filePct:0}%");
                });

            IProgress<RemoteDownloadProgress> CreateDownloadJobProgress(int workerId, RemoteTransferJob job) =>
                new Progress<RemoteDownloadProgress>(p =>
                {
                    Volatile.Write(ref workerInflightBytes[workerId], Math.Max(0, p.BytesTransferred));
                    var filePct = p.TotalBytes > 0 ? p.Percent : 0d;
                    SetWorker(workerId, "Busy", job.DisplayName, $"{filePct:0}%");
                    Publish("Transferring", $"W{workerId + 1} {verb} {job.DisplayName} · {filePct:0}%");
                });

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
                        var budget = TransferTimeoutPolicy.Describe(job.SizeBytes);
                        SetWorker(workerId, "Busy", job.DisplayName, $"budget {budget}");
                        Publish("Transferring", $"W{workerId + 1} {verb} {job.DisplayName} · budget {budget}");
                        try
                        {
                            if (download)
                            {
                                var localDir = Path.GetDirectoryName(job.LocalPath);
                                if (!string.IsNullOrWhiteSpace(localDir))
                                {
                                    Directory.CreateDirectory(localDir);
                                }

                                Volatile.Write(ref workerInflightBytes[workerId], 0);
                                await service.DownloadFileAsync(
                                    job.RemotePath,
                                    job.LocalPath,
                                    CreateDownloadJobProgress(workerId, job),
                                    cancellationToken);
                                Volatile.Write(ref workerInflightBytes[workerId], 0);
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
                                        succeeded.Add(job);
                                        jobErrors.TryRemove(job.RemotePath, out _);
                                        onJobDone?.Invoke(job, true, null);
                                        SetWorker(workerId, "Idle", $"skipped {job.DisplayName}", "exists");
                                        Publish("Transferring", $"W{workerId + 1} skipped {job.DisplayName}");
                                        continue;
                                    }
                                }

                                Volatile.Write(ref workerInflightBytes[workerId], 0);
                                await service.UploadLocalFileAsync(
                                    job.LocalPath,
                                    job.RemotePath,
                                    CreateUploadJobProgress(workerId, job),
                                    cancellationToken);
                                Volatile.Write(ref workerInflightBytes[workerId], 0);
                            }

                            Interlocked.Add(ref transferredBytes, Math.Max(0, job.SizeBytes));
                            Interlocked.Increment(ref completed);
                            filesDoneByWorker[workerId]++;
                            succeeded.Add(job);
                            jobErrors.TryRemove(job.RemotePath, out _);
                            onJobDone?.Invoke(job, true, null);
                            SetWorker(workerId, "Idle", $"done {job.DisplayName}", $"{filesDoneByWorker[workerId]} files");
                            Publish("Transferring", $"W{workerId + 1} finished {job.DisplayName}");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            Volatile.Write(ref workerInflightBytes[workerId], 0);
                            await TryRemovePartialAsync(service, job, download);
                            SetWorker(workerId, "Cancelled", job.DisplayName, string.Empty);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Volatile.Write(ref workerInflightBytes[workerId], 0);
                            await TryRemovePartialAsync(service, job, download);
                            var detail = FormatJobError(job, ex);
                            jobErrors[job.RemotePath] = detail;
                            SetWorker(workerId, "Error", job.DisplayName, detail);
                            Publish("Transferring", $"W{workerId + 1} failed {job.DisplayName}: {detail}");
                        }
                    }

                    SetWorker(workerId, "Done", $"{filesDoneByWorker[workerId]} files", "batch done");
                    Publish("Transferring", $"W{workerId + 1} batch done ({filesDoneByWorker[workerId]} files)");
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

            async Task RunPoolAsync(IReadOnlyList<RemoteTransferJob> batch, int poolWorkers, string label)
            {
                if (batch.Count == 0)
                {
                    return;
                }

                queue = new ConcurrentQueue<RemoteTransferJob>(batch);
                var n = Math.Clamp(poolWorkers, 1, batch.Count);
                for (var i = 0; i < workers; i++)
                {
                    Volatile.Write(ref workerInflightBytes[i], 0);
                }

                Publish(label, $"{label}: {batch.Count} files · {n} workers");
                await Task.WhenAll(Enumerable.Range(0, n).Select(WorkerAsync));
            }

            if (smallJobs.Count > 0)
            {
                await RunPoolAsync(smallJobs, workers, "Small files");
            }

            if (largeJobs.Count > 0)
            {
                var largeWorkers = Math.Clamp(workers, 1, largeJobs.Count);
                await RunPoolAsync(largeJobs, largeWorkers, "Large files");
                var retry = largeJobs.Where(job => !succeeded.Contains(job)).ToList();
                if (retry.Count > 0)
                {
                    await RunPoolAsync(retry, 1, "Retry large files");
                }
            }

            foreach (var failed in list.Where(job => !succeeded.Contains(job)))
            {
                if (jobErrors.TryGetValue(failed.RemotePath, out var detail))
                {
                    onJobDone?.Invoke(failed, false, detail);
                }
            }

            var failedJobs = list.Where(job => !succeeded.Contains(job)).ToList();
            List<RemoteTransferJob> verifyMissing;
            if (download)
            {
                Publish("Verifying", "Checking local files…");
                verifyMissing = succeeded.Where(job => !File.Exists(job.LocalPath)).ToList();
            }
            else
            {
                Publish("Verifying", "Checking remote files…");
                verifyMissing = await VerifyRemoteFilesAsync(profile, succeeded.ToList(), cancellationToken);
            }

            var missing = failedJobs.Concat(verifyMissing).Distinct().ToList();
            var result = new ParallelTransferResult
            {
                WorkerCount = workers,
                Completed = Volatile.Read(ref completed),
                Skipped = Volatile.Read(ref skipped),
                Missing = missing,
                Errors = list
                    .Where(job => !succeeded.Contains(job))
                    .Select(job => jobErrors.TryGetValue(job.RemotePath, out var detail)
                        ? $"{job.DisplayName}: {detail}"
                        : $"{job.DisplayName}: Upload failed")
                    .ToList()
            };
            Publish(
                "Finished",
                result.IsComplete
                    ? $"All {result.Completed} files verified with {result.WorkerCount} workers"
                    : $"Finished with gaps: {result.Completed}/{list.Count}, failed {result.Errors.Count}");
            return result;
        }

        private static async Task<List<RemoteTransferJob>> VerifyRemoteFilesAsync(
            ConnectionProfile profile,
            IReadOnlyList<RemoteTransferJob> jobs,
            CancellationToken cancellationToken)
        {
            var missing = new List<RemoteTransferJob>();
            if (jobs.Count == 0)
            {
                return missing;
            }

            var service = RemoteFileServiceFactory.Create(profile);
            try
            {
                await service.ConnectAsync(profile, cancellationToken);
                foreach (var job in jobs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var stat = await service.GetFileStatAsync(job.RemotePath, cancellationToken);
                        // Only a directory clash is a confident miss. Exists=false is often a listing flake.
                        if (stat.Exists && stat.IsDirectory)
                        {
                            missing.Add(job);
                        }
                    }
                    catch
                    {
                        // Listing/STAT flake must not mark a successful upload as missing.
                    }
                }
            }
            catch
            {
                return new List<RemoteTransferJob>();
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

        private static string FormatJobError(RemoteTransferJob job, Exception ex)
        {
            var root = RemoteTransferErrorFormatter.ResolveRootCause(ex);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = ex.Message;
            }

            if (ex is TimeoutException || ContainsCancel(ex))
            {
                return string.IsNullOrWhiteSpace(root)
                    ? TransferTimeoutPolicy.StalledMessage(job.SizeBytes)
                    : root;
            }

            return $"{root} Partial removed — retry this file.";
        }

        private static bool ContainsCancel(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException or TaskCanceledException)
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task TryRemovePartialAsync(
            IRemoteFileService service,
            RemoteTransferJob job,
            bool download)
        {
            try
            {
                if (download)
                {
                    if (!string.IsNullOrWhiteSpace(job.LocalPath) && File.Exists(job.LocalPath))
                    {
                        File.Delete(job.LocalPath);
                    }

                    return;
                }

                await service.DeleteAsync(job.RemotePath, isDirectory: false, CancellationToken.None);
            }
            catch
            {
                try
                {
                    await service.DeleteAsync(job.RemotePath, isDirectory: true, CancellationToken.None);
                }
                catch
                {
                    // Partial cleanup is best-effort.
                }
            }
        }
    }
}
