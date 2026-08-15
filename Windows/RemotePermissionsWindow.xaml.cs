using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services.Remote;
using CheckBox = System.Windows.Controls.CheckBox;

namespace GitDeployPro.Windows
{
    public partial class RemotePermissionsWindow
    {
        private readonly bool _isFolder;
        private readonly Func<CancellationToken, Task<RemoteUnixPermissionInfo>> _load;
        private readonly Func<int, CancellationToken, Task> _apply;
        private readonly CheckBox[] _checks;
        private bool _suppressSync;
        private bool _canChange = true;
        private bool _busy;
        private CancellationTokenSource? _cts;

        public bool PermissionsChanged { get; private set; }

        public RemotePermissionsWindow(
            string name,
            string fullPath,
            bool isFolder,
            string protocol,
            Func<CancellationToken, Task<RemoteUnixPermissionInfo>> load,
            Func<int, CancellationToken, Task> apply)
        {
            InitializeComponent();
            _isFolder = isFolder;
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _checks =
            [
                OwnerReadCheck, OwnerWriteCheck, OwnerExecuteCheck,
                GroupReadCheck, GroupWriteCheck, GroupExecuteCheck,
                OthersReadCheck, OthersWriteCheck, OthersExecuteCheck
            ];

            var kind = isFolder ? "Folder" : "File";
            HeaderText.Text = $"{kind} '{name}' Permissions";
            Title = HeaderText.Text;
            FolderHintText.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            if (isFolder)
            {
                FolderHintText.Text = string.IsNullOrWhiteSpace(protocol)
                    ? "Applies to this folder only — not files inside."
                    : $"Applies to this folder only — not files inside. ({protocol})";
            }

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await LoadCurrentAsync();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task LoadCurrentAsync()
        {
            SetBusy(true, "Reading permissions…");
            ClearReason();
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var info = await _load(_cts.Token);
                if (!info.Exists)
                {
                    _canChange = false;
                    ApplyMode(_isFolder ? 0b111101101 : 0b110100100, enabled: false);
                    ShowReason(info.Reason ?? "This file or folder was not found on the server.", isError: true);
                    return;
                }

                _canChange = info.CanChange;
                var fallback = _isFolder ? Convert.ToInt32("755", 8) : Convert.ToInt32("644", 8);
                ApplyMode(info.CanReadMode ? info.Mode : fallback, enabled: _canChange);
                if (!string.IsNullOrWhiteSpace(info.Reason))
                {
                    ShowReason(info.Reason, isError: !info.CanChange);
                }
            }
            catch (OperationCanceledException)
            {
                // Closed while reading.
            }
            catch (Exception ex)
            {
                _canChange = false;
                ShowReason(UnixPermissionMode.ExplainError(ex, usesSsh: false), isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyMode(int mode, bool enabled)
        {
            _suppressSync = true;
            try
            {
                OwnerReadCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OwnerRead);
                OwnerWriteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OwnerWrite);
                OwnerExecuteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OwnerExecute);
                GroupReadCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.GroupRead);
                GroupWriteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.GroupWrite);
                GroupExecuteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.GroupExecute);
                OthersReadCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OthersRead);
                OthersWriteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OthersWrite);
                OthersExecuteCheck.IsChecked = UnixPermissionMode.Has(mode, UnixPermissionMode.OthersExecute);
                OctalBox.Text = UnixPermissionMode.ToOctal(mode);
                SymbolicText.Text = UnixPermissionMode.ToSymbolic(mode);
            }
            finally
            {
                _suppressSync = false;
            }

            SetInputsEnabled(enabled);
        }

        private int ReadModeFromChecks()
        {
            var mode = 0;
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OwnerRead, OwnerReadCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OwnerWrite, OwnerWriteCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OwnerExecute, OwnerExecuteCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.GroupRead, GroupReadCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.GroupWrite, GroupWriteCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.GroupExecute, GroupExecuteCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OthersRead, OthersReadCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OthersWrite, OthersWriteCheck.IsChecked == true);
            mode = UnixPermissionMode.Set(mode, UnixPermissionMode.OthersExecute, OthersExecuteCheck.IsChecked == true);
            return mode;
        }

        private void Permission_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSync)
            {
                return;
            }

            var mode = ReadModeFromChecks();
            _suppressSync = true;
            try
            {
                OctalBox.Text = UnixPermissionMode.ToOctal(mode);
                SymbolicText.Text = UnixPermissionMode.ToSymbolic(mode);
            }
            finally
            {
                _suppressSync = false;
            }
        }

        private void OctalBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSync)
            {
                return;
            }

            if (!UnixPermissionMode.TryParseOctal(OctalBox.Text, out var mode))
            {
                return;
            }

            ApplyMode(mode, enabled: _canChange && !_busy);
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (!_canChange || _busy)
            {
                return;
            }

            if (!UnixPermissionMode.TryParseOctal(OctalBox.Text, out var mode))
            {
                ShowReason("Enter a 3-digit octal mode such as 644 or 755.", isError: true);
                OctalBox.Focus();
                return;
            }

            SetBusy(true, "Applying permissions…");
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                await _apply(mode, _cts.Token);
                PermissionsChanged = true;
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                // Closed while applying.
            }
            catch (Exception ex)
            {
                ShowReason(ex.InnerException != null
                    ? UnixPermissionMode.ExplainError(ex.InnerException, usesSsh: false)
                    : (string.IsNullOrWhiteSpace(ex.Message) ? UnixPermissionMode.ExplainError(ex, usesSsh: false) : ex.Message),
                    isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            SetInputsEnabled(_canChange && !busy);
            ApplyButton.IsEnabled = _canChange && !busy;
            ApplyButton.Content = busy ? "Working…" : "Apply";
            if (busy && !string.IsNullOrWhiteSpace(status))
            {
                ShowReason(status, isError: false);
            }
        }

        private void SetInputsEnabled(bool enabled)
        {
            foreach (var check in _checks)
            {
                check.IsEnabled = enabled;
            }

            OctalBox.IsEnabled = enabled;
        }

        private void ShowReason(string message, bool isError)
        {
            ReasonText.Text = message;
            ReasonBanner.Visibility = Visibility.Visible;
            var surface = TryFindResource(isError ? "Status.ErrorSurface" : "Status.WarningSurface") as System.Windows.Media.Brush;
            var border = TryFindResource(isError ? "Status.Error" : "Status.Warning") as System.Windows.Media.Brush;
            var foreground = TryFindResource(isError ? "Status.Error" : "Status.Warning") as System.Windows.Media.Brush;
            if (surface != null)
            {
                ReasonBanner.Background = surface;
            }

            if (border != null)
            {
                ReasonBanner.BorderBrush = border;
            }

            if (foreground != null)
            {
                ReasonText.Foreground = foreground;
            }
        }

        private void ClearReason()
        {
            ReasonBanner.Visibility = Visibility.Collapsed;
            ReasonText.Text = string.Empty;
        }
    }
}
