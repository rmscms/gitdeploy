using System;
using System.IO;
using System.IO.Compression;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public class BackupHealthService
    {
        public BackupHealthReport Verify(string path, bool isZip)
        {
            var report = new BackupHealthReport();
            if (string.IsNullOrWhiteSpace(path))
            {
                report.IsHealthy = false;
                report.Details = "File path missing.";
                return report;
            }

            try
            {
                if (!File.Exists(path))
                {
                    report.IsHealthy = false;
                    report.Details = "Backup file not found.";
                    return report;
                }

                if (isZip)
                {
                    using var archive = ZipFile.OpenRead(path);
                    if (archive.Entries.Count == 0)
                    {
                        throw new InvalidOperationException("Archive contains no entries.");
                    }

                    var firstEntry = archive.Entries[0];
                    using var entryStream = firstEntry.Open();
                    Span<byte> buffer = stackalloc byte[256];
                    entryStream.Read(buffer);
                }
                else
                {
                    using var stream = File.OpenRead(path);
                    if (stream.Length < 32)
                    {
                        throw new InvalidOperationException("Backup file too small.");
                    }

                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var head = reader.ReadLine() ?? string.Empty;
                    if (!head.Contains("GitDeploy Pro", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Missing GitDeploy Pro header.");
                    }

                    if (stream.Length > 4096)
                    {
                        stream.Seek(-4096, SeekOrigin.End);
                    }
                    else
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                    }

                    using var tailReader = new StreamReader(stream);
                    var tailContent = tailReader.ReadToEnd();
                    if (!tailContent.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) &&
                        !tailContent.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("SQL tail missing expected statements.");
                    }
                }

                report.IsHealthy = true;
                report.Details = "Structure validated.";
            }
            catch (Exception ex)
            {
                report.IsHealthy = false;
                report.Details = ex.Message;
            }

            return report;
        }
    }
}

