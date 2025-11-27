using System.Windows;
using System.Windows.Media;
using System.Windows.Controls; // Explicitly using WPF controls

namespace GitDeployPro.Controls
{
    public partial class ModernMessageBox : Window
    {
        public bool Result { get; private set; } = false;

        public ModernMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
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
                    OkButton.Content = "OK";
                    break;
                case MessageBoxButton.YesNo:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = "No";
                    OkButton.Content = "Yes";
                    break;
                case MessageBoxButton.OKCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = "Cancel";
                    OkButton.Content = "OK";
                    break;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            this.Close();
        }

        public static bool Show(string message, string title = "Notification", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information)
        {
            var msgBox = new ModernMessageBox(message, title, buttons, image);
            msgBox.ShowDialog();
            return msgBox.Result;
        }
    }
}
