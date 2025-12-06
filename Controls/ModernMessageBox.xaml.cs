using System.Windows;
using System.Windows.Media;
using System.Windows.Controls; // Explicitly using WPF controls

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

            // Set Icon & Color
            switch (image)
            {
                case MessageBoxImage.Error: 
                    IconText.Text = "❌"; 
                    IconText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 115, 115)); 
                    TitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 115, 115));
                    break;
                case MessageBoxImage.Warning: 
                    IconText.Text = "⚠️"; 
                    IconText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 183, 77)); 
                    TitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 183, 77));
                    break;
                case MessageBoxImage.Question: 
                    IconText.Text = "❓"; 
                    IconText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 181, 246)); 
                    break;
                case MessageBoxImage.Information: 
                    if (title.Contains("Success"))
                    {
                        IconText.Text = "✅";
                        IconText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); 
                        TitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                    }
                    else
                    {
                        IconText.Text = "ℹ️"; 
                        IconText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 181, 246)); 
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

        public static bool Show(string message, string title = "Notification", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information, string? primaryText = null, string? secondaryText = null, string? cancelText = null)
        {
            var msgBox = new ModernMessageBox(message, title, buttons, image, primaryText, secondaryText, cancelText);
            msgBox.ShowDialog();
            return msgBox.Result;
        }

        public static MessageBoxResult ShowWithResult(string message, string title = "Notification", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information, string? primaryText = null, string? secondaryText = null, string? cancelText = null)
        {
            var msgBox = new ModernMessageBox(message, title, buttons, image, primaryText, secondaryText, cancelText);
            msgBox.ShowDialog();
            return msgBox.MessageResult;
        }
    }
}
