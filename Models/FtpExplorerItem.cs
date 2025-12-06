using System;
using System.ComponentModel;
using System.IO;
using FluentFTP;
using Renci.SshNet.Sftp;

namespace GitDeployPro.Models
{
    public class FtpExplorerItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string SizeDisplay { get; set; } = string.Empty;
        public string ModifiedDisplay { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Permissions { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsLink { get; set; }
        public string Icon { get; set; } = "📄";
        public string IconColor { get; set; } = "#FFFFFF";
        public bool IsParentLink { get; set; }
        public long SizeBytes { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public static FtpExplorerItem FromFtp(FtpListItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var type = item.Type == FtpObjectType.Directory ? "Dir" :
                       item.Type == FtpObjectType.Link ? "Link" : "File";
            var permissions = item.Chmod > 0
                ? Convert.ToString(item.Chmod, 8)
                : item.RawPermissions;
            var icon = GetIconGlyph(item.Name, type);

            return new FtpExplorerItem
            {
                Name = item.Name ?? string.Empty,
                Type = type,
                SizeDisplay = item.Type == FtpObjectType.Directory ? "-" : FormatSize(item.Size),
                ModifiedDisplay = item.Modified == DateTime.MinValue
                    ? string.Empty
                    : item.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Owner = string.IsNullOrWhiteSpace(item.RawOwner) ? "?" : item.RawOwner,
                Group = string.IsNullOrWhiteSpace(item.RawGroup) ? "?" : item.RawGroup,
                Permissions = string.IsNullOrWhiteSpace(permissions) ? "?" : permissions,
                FullPath = item.FullName ?? item.Name ?? string.Empty,
                IsDirectory = item.Type == FtpObjectType.Directory,
                IsLink = item.Type == FtpObjectType.Link,
                Icon = icon,
                IconColor = GetIconColor(item.Name, type),
                SizeBytes = item.Size
            };
        }

        public static FtpExplorerItem FromSftp(ISftpFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            var type = file.IsDirectory ? "Dir" :
                       file.IsSymbolicLink ? "Link" : "File";
            var perms = file.Attributes != null ? file.Attributes.ToString() : string.Empty;
            var icon = GetIconGlyph(file.Name, type);

            return new FtpExplorerItem
            {
                Name = file.Name ?? string.Empty,
                Type = type,
                SizeDisplay = file.IsDirectory ? "-" : FormatSize(file.Length),
                ModifiedDisplay = file.LastWriteTime == DateTime.MinValue
                    ? string.Empty
                    : file.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Owner = file.UserId.ToString(),
                Group = file.GroupId.ToString(),
                Permissions = string.IsNullOrWhiteSpace(perms) ? "?" : perms,
                FullPath = file.FullName ?? file.Name ?? string.Empty,
                IsDirectory = file.IsDirectory,
                IsLink = file.IsSymbolicLink,
                Icon = icon,
                IconColor = GetIconColor(file.Name, type),
                SizeBytes = file.Length
            };
        }

        public static FtpExplorerItem CreateParentEntry(string parentPath)
        {
            return new FtpExplorerItem
            {
                Name = ".. (Up)",
                Type = "Dir",
                SizeDisplay = "-",
                ModifiedDisplay = string.Empty,
                Owner = string.Empty,
                Group = string.Empty,
                Permissions = string.Empty,
                FullPath = parentPath,
                IsDirectory = true,
                IsParentLink = true,
                Icon = "↩",
                IconColor = "#FFD54F"
            };
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;
            while (size >= 1024 && order < units.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {units[order]}";
        }

        public static string FormatSizeReadable(long bytes) => FormatSize(bytes);

        private static string GetIconGlyph(string? name, string type)
        {
            if (type == "Dir") return "📁";
            if (type == "Link") return "🔗";

            var ext = Path.GetExtension(name ?? string.Empty)?.ToLowerInvariant();
            return ext switch
            {
                ".php" => "🐘",
                ".html" or ".htm" => "</>",
                ".css" => "🎨",
                ".js" or ".jsx" or ".ts" or ".tsx" => "{}",
                ".json" => "{}",
                ".cs" => "#",
                ".sql" => "🗄",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "🖼",
                ".zip" or ".rar" or ".7z" => "🗜",
                ".pdf" => "📕",
                ".txt" or ".log" => "📄",
                _ => "📄"
            };
        }

        private static string GetIconColor(string? name, string type)
        {
            if (type == "Dir") return "#FFCA28";
            if (type == "Link") return "#90CAF9";

            var ext = Path.GetExtension(name ?? string.Empty)?.ToLowerInvariant();
            return ext switch
            {
                ".php" => "#A762FF",
                ".html" or ".htm" => "#FF7043",
                ".css" => "#4FC3F7",
                ".js" or ".jsx" or ".ts" or ".tsx" => "#FFD54F",
                ".json" => "#26C6DA",
                ".cs" => "#66BB6A",
                ".sql" => "#FFB74D",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "#BA68C8",
                ".zip" or ".rar" or ".7z" => "#8D6E63",
                ".pdf" => "#EF5350",
                ".txt" or ".log" => "#B0BEC5",
                _ => "#E0E0E0"
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


