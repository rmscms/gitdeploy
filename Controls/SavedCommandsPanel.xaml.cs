using System;
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

        public event EventHandler? CloseRequested;
        public event EventHandler<string>? PresentationModeRequested;

        public Action<string>? InjectCommand { get; set; }

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
            if (PresetComboBox == null)
            {
                return;
            }

            var previous = PresetComboBox.SelectedValue?.ToString();
            _presets = TerminalPresetStore.LoadPresets();
            PresetComboBox.ItemsSource = _presets;
            if (!string.IsNullOrEmpty(previous))
            {
                var match = _presets.FirstOrDefault(p => p.Id == previous);
                if (match != null)
                {
                    PresetComboBox.SelectedItem = match;
                }
            }

            if (PresetComboBox.SelectedIndex < 0 && _presets.Count > 0)
            {
                PresetComboBox.SelectedIndex = 0;
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

            // Keep one mode selected (radio-like).
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

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is TerminalCommandPreset preset &&
                !string.IsNullOrWhiteSpace(preset.Command))
            {
                Inject(preset.Command);
                return;
            }

            var typed = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(typed))
            {
                Inject(typed);
            }
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            var command = (PresetCommandBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command) &&
                PresetComboBox.SelectedItem is TerminalCommandPreset preset)
            {
                command = preset.Command;
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                Inject(command);
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

            var preset = new TerminalCommandPreset
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Command = command
            };
            _presets.Add(preset);
            TerminalPresetStore.SavePresets(_presets);
            PresetTitleBox.Text = string.Empty;
            PresetCommandBox.Text = string.Empty;
            PresetComboBox.SelectedItem = preset;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is not TerminalCommandPreset preset)
            {
                return;
            }

            var existing = _presets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing == null)
            {
                return;
            }

            _presets.Remove(existing);
            TerminalPresetStore.SavePresets(_presets);
        }

        private void Inject(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            InjectCommand?.Invoke(command);
        }

        public static string NormalizeMode(string? mode)
            => string.Equals(mode, ModeFloat, StringComparison.OrdinalIgnoreCase)
                ? ModeFloat
                : ModeDock;
    }
}
