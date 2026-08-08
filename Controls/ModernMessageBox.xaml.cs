using System.Windows;
using System.Windows.Media;
using System.Windows.Controls; // Explicitly using WPF controls
using GitDeployPro.Services;

namespace GitDeployPro.Controls
{
    public partial class ModernMessageBox : Window
    {
        public bool Result { get; private set; } = false;
        public MessageBoxResult MessageResult { get; private set; } = MessageBoxResult.None;

        private MessageBoxResult _primaryResult = MessageBoxResult.OK;
        private MessageBoxResult _secondaryResult = MessageBoxResult.None;
        private MessageBoxResult _cancelResult = MessageBoxResult.None;

        public ModernMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage image, string? primaryText = null, string? secondaryText = null, string? cancelText = null)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;

            // Icon + status colors from ThemeManager tokens (follow Default/Dark/custom packs).
            switch (image)
            {
                case MessageBoxImage.Error:
                    IconText.Text = "❌";
                    ApplyStatusBrush(IconText, TitleText, "Status.Error");
                    break;
                case MessageBoxImage.Warning:
                    IconText.Text = "⚠️";
                    ApplyStatusBrush(IconText, TitleText, "Status.Warning");
                    break;
                case MessageBoxImage.Question:
                    IconText.Text = "❓";
                    ApplyStatusBrush(IconText, null, "Status.Info");
                    break;
                case MessageBoxImage.Information:
                    if (title.Contains("Success", System.StringComparison.OrdinalIgnoreCase))
                    {
                        IconText.Text = "✅";
                        ApplyStatusBrush(IconText, TitleText, "Status.Success");
                    }
                    else
                    {
                        IconText.Text = "ℹ️";
                        ApplyStatusBrush(IconText, null, "Status.Info");
                    }
                    break;
                default:
                    IconText.Text = "📢";
                    break;
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    CancelButton.Visibility = Visibility.Collapsed;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                    _primaryResult = MessageBoxResult.OK;
                    break;
                case MessageBoxButton.YesNo:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "No" : secondaryText;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "Yes" : primaryText;
                    _primaryResult = MessageBoxResult.Yes;
                    _cancelResult = MessageBoxResult.No;
                    break;
                case MessageBoxButton.OKCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                    _primaryResult = MessageBoxResult.OK;
                    _cancelResult = MessageBoxResult.Cancel;
                    break;
                case MessageBoxButton.YesNoCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    ExtraButton.Visibility = Visibility.Visible;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "Yes" : primaryText;
                    ExtraButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "No" : secondaryText;
                    CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText;
                    _primaryResult = MessageBoxResult.Yes;
                    _secondaryResult = MessageBoxResult.No;
                    _cancelResult = MessageBoxResult.Cancel;
                    break;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = _primaryResult;
            Result = MessageResult == MessageBoxResult.OK || MessageResult == MessageBoxResult.Yes;
            this.Close();
        }

        private void ExtraButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = _secondaryResult == MessageBoxResult.None ? MessageBoxResult.No : _secondaryResult;
            Result = false;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            MessageResult = _cancelResult == MessageBoxResult.None ? MessageBoxResult.Cancel : _cancelResult;
            Result = MessageResult == MessageBoxResult.OK || MessageResult == MessageBoxResult.Yes;
            this.Close();
        }

        private void ApplyStatusBrush(TextBlock icon, TextBlock? title, string resourceKey)
        {
            var brush = TryFindResource(resourceKey) as System.Windows.Media.Brush
                        ?? System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush;
            if (brush == null)
            {
                return;
            }

            icon.Foreground = brush;
            if (title != null)
            {
                title.Foreground = brush;
            }
        }

        private static void PrepareOwnerAndPlacement(ModernMessageBox msgBox, Window? owner, DependencyObject? context)
        {
            var resolvedOwner = WindowOwnerService.ResolveOwner(context, owner) ?? System.Windows.Application.Current?.MainWindow;
            if (resolvedOwner != null && !ReferenceEquals(msgBox, resolvedOwner))
            {
                WindowOwnerService.ApplyOwner(msgBox, resolvedOwner, centerOnOwner: true);
                msgBox.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            msgBox.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        public static bool Show(
            string message,
            string title = "Notification",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.Information,
            string? primaryText = null,
            string? secondaryText = null,
            string? cancelText = null,
            Window? owner = null,
            DependencyObject? context = null)
        {
            var msgBox = new ModernMessageBox(message, title, buttons, image, primaryText, secondaryText, cancelText);
            PrepareOwnerAndPlacement(msgBox, owner, context);
            msgBox.ShowDialog();
            return msgBox.Result;
        }

        public static MessageBoxResult ShowWithResult(
            string message,
            string title = "Notification",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.Information,
            string? primaryText = null,
            string? secondaryText = null,
            string? cancelText = null,
            Window? owner = null,
            DependencyObject? context = null)
        {
            var msgBox = new ModernMessageBox(message, title, buttons, image, primaryText, secondaryText, cancelText);
            PrepareOwnerAndPlacement(msgBox, owner, context);
            msgBox.ShowDialog();
            return msgBox.MessageResult;
        }
    }
}
