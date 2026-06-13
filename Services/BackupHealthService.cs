using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GitDeployPro.Models;
using SharpCompress.Readers;

namespace GitDeployPro.Services
{
    public class BackupHealthService
    {
        public BackupHealthReport Verify(string path, bool isCompressed)
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

                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".protected")
                {
                    VerifyEncryptedContainer(path);
                }
                else if (extension == ".zip")
                {
                    VerifyZip(path);
                }
                else if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyTarGz(path);
                }
                else if (extension == ".sql")
                {
                    VerifySql(path);
                }
                else if (isCompressed)
                {
                    // Legacy fallback in case extension is missing.
                    TryVerifyCompressedUnknown(path);
                }
                else
                {
                    VerifySql(path);
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

        private static void VerifySql(string path)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 64)
            {
                throw new InvalidOperationException("Backup file is too small.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
            var head = ReadChunk(reader, 8192);
            if (!ContainsSqlMarkers(head))
            {
                throw new InvalidOperationException("SQL header does not contain expected dump statements.");
            }

            stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
            using var tailReader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var tail = tailReader.ReadToEnd();
            if (!ContainsSqlMarkers(tail))
            {
                throw new InvalidOperationException("SQL tail does not contain expected dump statements.");
            }
        }

        private static void VerifyZip(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
            {
                throw new InvalidOperationException("Archive contains no entries.");
            }

            var sqlEntry = archive.Entries
                .FirstOrDefault(e => e.Length > 0 && e.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                           ?? archive.Entries.FirstOrDefault(e => e.Length > 0);

            if (sqlEntry == null)
            {
                throw new InvalidOperationException("Archive has no readable backup entry.");
            }

            using var entryStream = sqlEntry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true, 4096, leaveOpen: true);
            var sample = ReadChunk(reader, 8192);
            if (!ContainsSqlMarkers(sample))
            {
                throw new InvalidOperationException("Compressed SQL content is not recognizable.");
            }
        }

        private static void VerifyTarGz(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = ReaderFactory.Open(stream);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                using var entryStream = reader.OpenEntryStream();
                using var entryReader = new StreamReader(entryStream, Encoding.UTF8, true, 4096, leaveOpen: false);
                var sample = ReadChunk(entryReader, 8192);
                if (ContainsSqlMarkers(sample))
                {
                    return;
                }
            }

            throw new InvalidOperationException("No valid SQL content found in tar.gz archive.");
        }

        private static void TryVerifyCompressedUnknown(string path)
        {
            try
            {
                VerifyZip(path);
                return;
            }
            catch
            {
                // ignored - try tar.gz parser below
            }

            VerifyTarGz(path);
        }

        private static void VerifyEncryptedContainer(string path)
        {
            var info = new FileInfo(path);
            if (info.Length < 64)
            {
                throw new InvalidOperationException("Encrypted backup container is too small.");
            }
        }

        private static string ReadChunk(StreamReader reader, int maxChars)
        {
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }

        private static bool ContainsSqlMarkers(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.Contains("MySQL dump", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("LOCK TABLES", StringComparison.OrdinalIgnoreCase);
        }
    }
}

