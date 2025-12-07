using System;
using System.Windows;
using System.Windows.Threading;

namespace GitDeployPro.Windows
{
    public partial class ToastWindow : Window
    {
        private readonly DispatcherTimer _timer;

        public ToastWindow(string title, string message)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            Loaded += ToastWindow_Loaded;
            MouseLeftButtonUp += (_, _) => Close();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Close();
            };
        }

        private void ToastWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
            _timer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}

