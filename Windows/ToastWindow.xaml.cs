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
            var targetRect = SystemParameters.WorkArea;
            if (Owner != null && Owner.IsLoaded && Owner.WindowState != WindowState.Minimized)
            {
                var ownerWidth = Owner.ActualWidth > 0 ? Owner.ActualWidth : Owner.Width;
                var ownerHeight = Owner.ActualHeight > 0 ? Owner.ActualHeight : Owner.Height;
                if (ownerWidth > 0 && ownerHeight > 0)
                {
                    targetRect = new Rect(Owner.Left, Owner.Top, ownerWidth, ownerHeight);
                }
            }

            Left = targetRect.Right - Width - 20;
            Top = targetRect.Bottom - Height - 20;
            _timer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}

