using System;
using System.IO;

namespace GitDeployPro.Services
{
    public static class BackupArtifactNaming
    {
        private const string TimestampFormat = "yy_MM_dd_HH_mm";

        public static string CreateArtifactBaseName(string databaseName, DateTime? timestamp = null)
        {
            var safeDbName = SanitizeToken(string.IsNullOrWhiteSpace(databaseName) ? "database" : databaseName);
            var timeToken = (timestamp ?? DateTime.Now).ToString(TimestampFormat);
            return $"{safeDbName}_{timeToken}";
        }

        public static bool TryMarkAsVerified(string sourcePath, out string finalPath, out string message)
        {
            finalPath = sourcePath ?? string.Empty;
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                message = "Verification tag skipped: artifact file not found.";
                return false;
            }

            var targetPath = BuildVerifiedPath(sourcePath);
            if (string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                finalPath = sourcePath;
                message = "Artifact already has verify tag.";
                return true;
            }

            targetPath = EnsureUniqueFilePath(targetPath);
            File.Move(sourcePath, targetPath);
            finalPath = targetPath;
            message = $"Artifact marked as verified: {Path.GetFileName(targetPath)}";
            return true;
        }

        private static string BuildVerifiedPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            var fileName = Path.GetFileName(sourcePath);
            var (baseName, tailExtension) = SplitKnownTail(fileName);

            if (baseName.EndsWith("_verify", StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }

            var verifiedFileName = $"{baseName}_verify{tailExtension}";
            return Path.Combine(directory, verifiedFileName);
        }

        private static (string baseName, string tailExtension) SplitKnownTail(string fileName)
        {
            var knownTails = new[]
            {
                ".sql.gz.protected",
                ".tar.gz.protected",
                ".zip.protected",
                ".sql.protected",
                ".sql.gz",
                ".tar.gz",
                ".zip",
                ".sql"
            };

            foreach (var tail in knownTails)
            {
                if (fileName.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return (fileName[..^tail.Length], tail);
                }
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return (fileName, string.Empty);
            }

            return (fileName[..^extension.Length], extension);
        }

        private static string SanitizeToken(string token)
        {
            var value = token.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            value = value.Replace(' ', '_');
            while (value.Contains("__", StringComparison.Ordinal))
            {
                value = value.Replace("__", "_", StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(value) ? "database" : value;
        }

        private static string EnsureUniqueFilePath(string preferredPath)
        {
            if (!File.Exists(preferredPath))
            {
                return preferredPath;
            }

            var directory = Path.GetDirectoryName(preferredPath) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(preferredPath);
            var extension = Path.GetExtension(preferredPath);
            var index = 1;
            while (true)
            {
                var candidate = Path.Combine(directory, $"{fileName}_{index:00}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }
    }
}
