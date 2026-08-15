using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitDeployPro.Models;
using GitDeployPro.Services;

namespace GitDeployPro.Windows
{
    public partial class LocalItemPropertiesWindow
    {
        private readonly string _fullPath;
        private readonly bool _isFolder;
        private readonly bool _isRemote;
        private readonly Func<CancellationToken, Task<RemoteLiveRefresh>>? _loadRemoteLive;
        private CancellationTokenSource? _folderScanCts;

        public LocalItemPropertiesWindow(string fullPath, bool isFolder)
        {
            InitializeComponent();
            _fullPath = fullPath ?? string.Empty;
            _isFolder = isFolder;
            _isRemote = false;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        public LocalItemPropertiesWindow(RemoteSnapshot snapshot)
        {
            InitializeComponent();
            ArgumentNullException.ThrowIfNull(snapshot);
            _fullPath = snapshot.FullPath ?? string.Empty;
            _isFolder = snapshot.IsFolder;
            _isRemote = true;
            _loadRemoteLive = snapshot.LoadLive;
            ApplyRemoteSnapshot(snapshot);
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        public sealed class RemoteSnapshot
        {
            public string Name { get; init; } = string.Empty;
            public string FullPath { get; init; } = string.Empty;
            public string Location { get; init; } = string.Empty;
            public bool IsFolder { get; init; }
            public string Protocol { get; init; } = "FTP";
            public long SizeBytes { get; init; }
            public DateTime? CreatedUtc { get; init; }
            public DateTime? ModifiedUtc { get; init; }
            public string? ModifiedLabel { get; init; }
            public Func<CancellationToken, Task<RemoteLiveRefresh>>? LoadLive { get; init; }
        }

        public sealed class RemoteLiveRefresh
        {
            public RemoteFileStat? Stat { get; init; }
            public IReadOnlyList<RemoteDirectoryEntry>? Children { get; init; }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await LoadPropertiesAsync();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _folderScanCts?.Cancel();
            _folderScanCts?.Dispose();
            _folderScanCts = null;
        }

        private async Task LoadPropertiesAsync()
        {
            try
            {
                if (_isRemote)
                {
                    await RefreshRemoteLiveAsync();
                    return;
                }

                if (_isFolder)
                {
                    LoadFolderBasics();
                    await LoadFolderSizeAsync();
                    return;
                }

                LoadFileBasics();
            }
            catch (Exception ex)
            {
                SizeText.Text = "Unavailable";
                CreatedText.Text = "Not available";
                ModifiedText.Text = "Not available";
                AttributesText.Text = ex.Message;
            }
        }

        private void ApplyRemoteSnapshot(RemoteSnapshot snapshot)
        {
            var protocol = string.IsNullOrWhiteSpace(snapshot.Protocol) ? "FTP" : snapshot.Protocol.Trim();
            IconText.Text = snapshot.IsFolder ? "📁" : "📄";
            NameText.Text = string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.FullPath : snapshot.Name;
            KindText.Text = snapshot.IsFolder ? $"{protocol} folder" : $"{protocol} file";
            Title = $"{NameText.Text} — Properties";
            LocationText.Text = string.IsNullOrWhiteSpace(snapshot.Location) ? "/" : snapshot.Location;
            PathText.Text = string.IsNullOrWhiteSpace(snapshot.FullPath) ? "—" : snapshot.FullPath;
            SizeText.Text = snapshot.IsFolder
                ? "Calculating…"
                : FormatSize(Math.Max(0, snapshot.SizeBytes));
            CreatedText.Text = FormatUtcTimestamp(snapshot.CreatedUtc);
            ModifiedText.Text = snapshot.ModifiedUtc.HasValue
                ? FormatUtcTimestamp(snapshot.ModifiedUtc)
                : (string.IsNullOrWhiteSpace(snapshot.ModifiedLabel) ? "Not available" : snapshot.ModifiedLabel);
            AttributesText.Text = snapshot.IsFolder ? $"{protocol} folder" : $"{protocol} file";

            if (snapshot.IsFolder)
            {
                ContainsLabel.Visibility = Visibility.Visible;
                ContainsText.Visibility = Visibility.Visible;
                ContainsText.Text = "Calculating…";
                ExtensionLabel.Visibility = Visibility.Collapsed;
                ExtensionText.Visibility = Visibility.Collapsed;
            }
            else
            {
                var extension = Path.GetExtension(snapshot.Name);
                ExtensionText.Text = string.IsNullOrWhiteSpace(extension) ? "—" : extension;
            }
        }

        private async Task RefreshRemoteLiveAsync()
        {
            if (_loadRemoteLive == null)
            {
                if (_isFolder)
                {
                    SizeText.Text = "Not available";
                    ContainsText.Text = "Not available";
                }

                return;
            }

            _folderScanCts?.Cancel();
            _folderScanCts?.Dispose();
            _folderScanCts = new CancellationTokenSource();
            var token = _folderScanCts.Token;

            try
            {
                var live = await _loadRemoteLive(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var stat = live.Stat;
                if (stat != null && stat.Exists)
                {
                    if (!stat.IsDirectory)
                    {
                        SizeText.Text = FormatSize(Math.Max(0, stat.SizeBytes));
                    }

                    CreatedText.Text = FormatUtcTimestamp(stat.CreatedUtc);
                    ModifiedText.Text = FormatUtcTimestamp(stat.ModifiedUtc);
                }

                if (_isFolder)
                {
                    ApplyRemoteChildren(live.Children);
                }
            }
            catch (OperationCanceledException)
            {
                // Window closed while listing.
            }
            catch
            {
                if (_isFolder)
                {
                    SizeText.Text = "Not available";
                    ContainsText.Text = "Not available";
                }
            }
        }

        private void ApplyRemoteChildren(IReadOnlyList<RemoteDirectoryEntry>? children)
        {
            if (children == null)
            {
                SizeText.Text = "Not available";
                ContainsText.Text = "Not available";
                return;
            }

            var files = children.Count(entry => !entry.IsDirectory
                && !string.Equals(entry.Name, ".", StringComparison.Ordinal)
                && !string.Equals(entry.Name, "..", StringComparison.Ordinal));
            var folders = children.Count(entry => entry.IsDirectory
                && !string.Equals(entry.Name, ".", StringComparison.Ordinal)
                && !string.Equals(entry.Name, "..", StringComparison.Ordinal));
            var bytes = children
                .Where(entry => !entry.IsDirectory)
                .Sum(entry => Math.Max(0, entry.SizeBytes));

            SizeText.Text = files == 0 && bytes == 0
                ? "—"
                : $"{FormatSize(bytes)} in this folder";
            ContainsText.Text = $"{files:N0} file{(files == 1 ? string.Empty : "s")}, {folders:N0} folder{(folders == 1 ? string.Empty : "s")} (this folder)";
        }

        private static string FormatUtcTimestamp(DateTime? utcValue)
        {
            if (!utcValue.HasValue || utcValue.Value.Year < 1980)
            {
                return "Not available";
            }

            return AppTimeService.FormatLocalFromUtc(utcValue.Value);
        }

        private void LoadFileBasics()
        {
            var info = new FileInfo(_fullPath);
            IconText.Text = "📄";
            NameText.Text = info.Name;
            KindText.Text = "File";
            Title = $"{info.Name} — Properties";
            LocationText.Text = info.DirectoryName ?? "—";
            PathText.Text = info.FullName;
            SizeText.Text = info.Exists ? FormatSize(info.Length) : "File not found";
            CreatedText.Text = FormatTimestamp(TryGetTimestamp(() => info.CreationTime));
            ModifiedText.Text = FormatTimestamp(TryGetTimestamp(() => info.LastWriteTime));
            var extension = info.Extension;
            ExtensionText.Text = string.IsNullOrWhiteSpace(extension) ? "—" : extension;
            AttributesText.Text = FormatAttributes(info.Exists ? info.Attributes : 0, isFolder: false);
        }

        private void LoadFolderBasics()
        {
            var info = new DirectoryInfo(_fullPath);
            IconText.Text = "📁";
            NameText.Text = info.Name;
            KindText.Text = "Folder";
            Title = $"{info.Name} — Properties";
            LocationText.Text = info.Parent?.FullName ?? "—";
            PathText.Text = info.FullName;
            SizeText.Text = "Calculating…";
            ContainsLabel.Visibility = Visibility.Visible;
            ContainsText.Visibility = Visibility.Visible;
            ContainsText.Text = "Calculating…";
            CreatedText.Text = FormatTimestamp(TryGetTimestamp(() => info.CreationTime));
            ModifiedText.Text = FormatTimestamp(TryGetTimestamp(() => info.LastWriteTime));
            ExtensionLabel.Visibility = Visibility.Collapsed;
            ExtensionText.Visibility = Visibility.Collapsed;
            AttributesText.Text = FormatAttributes(info.Exists ? info.Attributes : 0, isFolder: true);
        }

        private async Task LoadFolderSizeAsync()
        {
            _folderScanCts?.Cancel();
            _folderScanCts?.Dispose();
            _folderScanCts = new CancellationTokenSource();
            var token = _folderScanCts.Token;
            var root = _fullPath;

            try
            {
                var result = await Task.Run(() => ScanFolder(root, token), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SizeText.Text = FormatSize(result.Bytes);
                ContainsText.Text = $"{result.Files:N0} file{(result.Files == 1 ? string.Empty : "s")}, {result.Folders:N0} folder{(result.Folders == 1 ? string.Empty : "s")}";
            }
            catch (OperationCanceledException)
            {
                // Window closed while scanning.
            }
            catch
            {
                SizeText.Text = "Not available";
                ContainsText.Text = "Not available";
            }
        }

        private static (long Bytes, int Files, int Folders) ScanFolder(string root, CancellationToken token)
        {
            long bytes = 0;
            var files = 0;
            var folders = 0;
            var pending = new System.Collections.Generic.Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        token.ThrowIfCancellationRequested();
                        files++;
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // Skip unreadable files.
                        }
                    }

                    foreach (var dir in Directory.EnumerateDirectories(current))
                    {
                        token.ThrowIfCancellationRequested();
                        folders++;
                        pending.Push(dir);
                    }
                }
                catch
                {
                    // Skip folders we cannot read.
                }
            }

            return (bytes, files, folders);
        }

        private static DateTime? TryGetTimestamp(Func<DateTime> read)
        {
            try
            {
                var value = read();
                if (value.Year < 1980)
                {
                    return null;
                }

                return value;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatTimestamp(DateTime? value)
        {
            if (!value.HasValue)
            {
                return "Not available";
            }

            return AppTimeService.FormatLocal(value.Value);
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes)";
        }

        private static string FormatAttributes(FileAttributes attributes, bool isFolder)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (isFolder)
            {
                parts.Add("Folder");
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                parts.Add("Read-only");
            }

            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                parts.Add("Hidden");
            }

            if (attributes.HasFlag(FileAttributes.System))
            {
                parts.Add("System");
            }

            return parts.Count == 0 ? "Normal" : string.Join(", ", parts);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_fullPath))
            {
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(_fullPath);
            }
            catch
            {
                // Clipboard can be locked by another app.
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
