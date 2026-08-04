using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Theme;

namespace GitDeployPro.Controls
{
    public partial class SavedCommandsPanel : System.Windows.Controls.UserControl
    {
        public const string ModeDock = "dock";
        public const string ModeFloat = "float";
        private ObservableCollection<TerminalCommandPreset> _presets = new();
        private bool _suppressModeToggle;
        private string _presentationMode = ModeDock;
        private string? _editingId;

        public event EventHandler? CloseRequested;
        public event EventHandler<string>? PresentationModeRequested;

        /// <summary>Type command into terminal and press Enter.</summary>
        public Action<string>? RunCommand { get; set; }

        /// <summary>Type command into terminal without Enter (partial / editable).</summary>
        public Action<string>? InsertCommand { get; set; }

        public SavedCommandsPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public string PresentationMode => _presentationMode;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TerminalPresetStore.PresetsChanged += OnPresetsChanged;
            Reload();
            ApplyTheme();
            SetPresentationMode(_presentationMode);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TerminalPresetStore.PresetsChanged -= OnPresetsChanged;
        }

        private void OnPresetsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(Reload);
                return;
            }

            Reload();
        }

        public void Reload()
        {
            _presets = TerminalPresetStore.LoadPresets();
            RefreshQuickList();
        }

        private void RefreshQuickList()
        {
            if (QuickList == null)
            {
                return;
            }

            var favorites = _presets
                .Where(p => p.IsFavorite)
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var others = _presets
                .Where(p => !p.IsFavorite)
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            QuickList.ItemsSource = favorites.Select(p => new QuickCommandRow(p)).ToList();
            if (QuickListEmptyHint != null)
            {
                if (favorites.Count == 0)
                {
                    QuickListEmptyHint.Text = _presets.Count == 0
                        ? "No saved commands yet. Add one below."
                        : "No favorites yet. Pick one below and press ★.";
                    QuickListEmptyHint.Visibility = Visibility.Visible;
                }
                else
                {
                    QuickListEmptyHint.Visibility = Visibility.Collapsed;
                }
            }

            if (MoreCommandsRow != null && MoreComboBox != null)
            {
                var hasOthers = others.Count > 0;
                MoreCommandsRow.Visibility = hasOthers ? Visibility.Visible : Visibility.Collapsed;
                MoreComboBox.ItemsSource = others;
                if (hasOthers)
                {
                    MoreComboBox.SelectedIndex = 0;
                }
            }
        }

        public void SetPresentationMode(string mode)
        {
            _presentationMode = NormalizeMode(mode);
            _suppressModeToggle = true;
            try
            {
                if (DockModeToggle != null)
                {
                    DockModeToggle.IsChecked = _presentationMode == ModeDock;
                }

                if (FloatModeToggle != null)
                {
                    FloatModeToggle.IsChecked = _presentationMode == ModeFloat;
                }
            }
            finally
            {
                _suppressModeToggle = false;
            }
        }

        public void ApplyTheme()
        {
            var theme = ThemeService.Instance;
            if (RootBorder != null)
            {
                RootBorder.Background = theme.GetTokenBrush(
                    "terminal.presetsDrawerBackground",
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0A0A0A")!);
                RootBorder.BorderBrush = theme.GetTokenBrush(
                    "terminal.presetsDrawerBorder",
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333333")!);
            }

            if (TitleText != null)
            {
                TitleText.Foreground = theme.GetTokenBrush(
                    "terminal.presetsHeaderForeground",
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")!);
            }

            if (ChipHost != null)
            {
                ChipHost.Background = theme.GetTokenBrush(
                    "terminal.presetsChipBackground",
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#141414")!);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void DockModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressModeToggle)
            {
                return;
            }

            _suppressModeToggle = true;
            try
            {
                FloatModeToggle.IsChecked = false;
            }
            finally
            {
                _suppressModeToggle = false;
            }

            PresentationModeRequested?.Invoke(this, ModeDock);
        }

        private void FloatModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressModeToggle)
            {
                return;
            }

            _suppressModeToggle = true;
            try
            {
                DockModeToggle.IsChecked = false;
            }
            finally
            {
                _suppressModeToggle = false;
            }

            PresentationModeRequested?.Invoke(this, ModeFloat);
        }

        private void DockModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressModeToggle)
            {
                return;
            }

            if (FloatModeToggle.IsChecked != true)
            {
                _suppressModeToggle = true;
                try
                {
                    DockModeToggle.IsChecked = true;
                }
                finally
                {
                    _suppressModeToggle = false;
                }
            }
        }

        private void FloatModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressModeToggle)
            {
                return;
            }

            if (DockModeToggle.IsChecked != true)
            {
                _suppressModeToggle = true;
                try
                {
                    FloatModeToggle.IsChecked = true;
                }
                finally
                {
                    _suppressModeToggle = false;
                }
            }
        }

        private void RowRun_Click(object sender, RoutedEventArgs e)
        {
            var preset = FindPresetFromSender(sender);
            if (preset != null && !string.IsNullOrWhiteSpace(preset.Command))
            {
                Run(preset.Command);
            }
        }

        private void RowInsert_Click(object sender, RoutedEventArgs e)
        {
            var preset = FindPresetFromSender(sender);
            if (preset != null && !string.IsNullOrWhiteSpace(preset.Command))
            {
                Insert(preset.Command);
            }
        }

        private void RowEdit_Click(object sender, RoutedEventArgs e)
        {
            var preset = FindPresetFromSender(sender);
            if (preset != null)
            {
                BeginEdit(preset);
            }
        }

        private void RowDelete_Click(object sender, RoutedEventArgs e)
        {
            var preset = FindPresetFromSender(sender);
            if (preset == null)
            {
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            if (string.Equals(_editingId, existing.Id, StringComparison.Ordinal))
            {
                ClearEditState();
            }

            _presets.Remove(existing);
            TerminalPresetStore.SavePresets(_presets);
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            var preset = FindPresetFromSender(sender);
            if (preset == null)
            {
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            existing.IsFavorite = !existing.IsFavorite;
            TerminalPresetStore.SavePresets(_presets);
        }

        private TerminalCommandPreset? FindPresetFromSender(object sender)
        {
            if (sender is not FrameworkElement element)
            {
                return null;
            }

            var id = element.Tag as string
                     ?? (element.DataContext as QuickCommandRow)?.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _presets.FirstOrDefault(p => p.Id == id);
        }

        private void MoreRun_Click(object sender, RoutedEventArgs e)
        {
            if (MoreComboBox?.SelectedItem is TerminalCommandPreset preset &&
                !string.IsNullOrWhiteSpace(preset.Command))
            {
                Run(preset.Command);
            }
        }

        private void MoreInsert_Click(object sender, RoutedEventArgs e)
        {
            if (MoreComboBox?.SelectedItem is TerminalCommandPreset preset &&
                !string.IsNullOrWhiteSpace(preset.Command))
            {
                Insert(preset.Command);
            }
        }

        private void MoreEdit_Click(object sender, RoutedEventArgs e)
        {
            if (MoreComboBox?.SelectedItem is TerminalCommandPreset preset)
            {
                BeginEdit(preset);
            }
        }

        private void MoreDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MoreComboBox?.SelectedItem is not TerminalCommandPreset preset)
            {
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            if (string.Equals(_editingId, existing.Id, StringComparison.Ordinal))
            {
                ClearEditState();
            }

            _presets.Remove(existing);
            TerminalPresetStore.SavePresets(_presets);
        }

        private void MoreFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (MoreComboBox?.SelectedItem is not TerminalCommandPreset preset)
            {
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            existing.IsFavorite = true;
            TerminalPresetStore.SavePresets(_presets);
        }

        private void SendRun_Click(object sender, RoutedEventArgs e)
        {
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(command))
            {
                Run(command);
            }
        }

        private void SendInsert_Click(object sender, RoutedEventArgs e)
        {
            var command = (PresetCommandBox.Text ?? string.Empty).TrimEnd();
            if (!string.IsNullOrWhiteSpace(command))
            {
                Insert(command);
            }
        }

        private void BeginEdit(TerminalCommandPreset preset)
        {
            _editingId = preset.Id;
            PresetTitleBox.Text = preset.Title ?? string.Empty;
            PresetCommandBox.Text = preset.Command ?? string.Empty;
            if (SaveButton != null)
            {
                SaveButton.Content = "Update";
                SaveButton.ToolTip = "Update this command";
            }

            PresetCommandBox.Focus();
            PresetCommandBox.CaretIndex = PresetCommandBox.Text?.Length ?? 0;
        }

        private void ClearEditState()
        {
            _editingId = null;
            if (SaveButton != null)
            {
                SaveButton.Content = "Save";
                SaveButton.ToolTip = "Save command preset";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var title = (PresetTitleBox.Text ?? string.Empty).Trim();
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(command))
            {
                ModernMessageBox.Show(
                    "Please enter both a title and a command.",
                    "Command Presets",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_editingId))
            {
                var existing = _presets.FirstOrDefault(p => p.Id == _editingId);
                if (existing != null)
                {
                    existing.Title = title;
                    existing.Command = command;
                    TerminalPresetStore.SavePresets(_presets);
                    PresetTitleBox.Text = string.Empty;
                    PresetCommandBox.Text = string.Empty;
                    ClearEditState();
                    return;
                }
            }

            var preset = new TerminalCommandPreset
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Command = command,
                IsFavorite = false
            };
            _presets.Add(preset);
            TerminalPresetStore.SavePresets(_presets);
            PresetTitleBox.Text = string.Empty;
            PresetCommandBox.Text = string.Empty;
            ClearEditState();
        }

        private void Run(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            RunCommand?.Invoke(command);
        }

        private void Insert(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            InsertCommand?.Invoke(command);
        }

        public static string NormalizeMode(string? mode)
            => string.Equals(mode, ModeFloat, StringComparison.OrdinalIgnoreCase)
                ? ModeFloat
                : ModeDock;

        private sealed class QuickCommandRow
        {
            public QuickCommandRow(TerminalCommandPreset preset)
            {
                Id = preset.Id;
                Title = string.IsNullOrWhiteSpace(preset.Title) ? "(untitled)" : preset.Title;
                Command = preset.Command ?? string.Empty;
                IsFavorite = preset.IsFavorite;
                FavoriteGlyph = preset.IsFavorite ? "★" : "☆";
                FavoriteBrush = preset.IsFavorite
                    ? (System.Windows.Application.Current?.TryFindResource("Status.Warning") as System.Windows.Media.Brush
                       ?? new SolidColorBrush(System.Windows.Media.Colors.Goldenrod))
                    : (System.Windows.Application.Current?.TryFindResource("Text.Muted") as System.Windows.Media.Brush
                       ?? System.Windows.Media.Brushes.Gray);
            }

            public string Id { get; }
            public string Title { get; }
            public string Command { get; }
            public bool IsFavorite { get; }
            public string FavoriteGlyph { get; }
            public System.Windows.Media.Brush FavoriteBrush { get; }
        }
    }
}
