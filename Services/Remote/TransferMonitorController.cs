using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Controls;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public sealed class TransferMonitorController
    {
        private TransferMonitorPanel? _panel;
        private FrameworkElement? _placementTarget;
        private ContentControl? _restingHost;
        private Window? _overlayWindow;
        private bool _overlayOpen;
        private Grid? _backdrop;
        private int _sessionVersion;
        private const double ModalWidth = 480;

        public void Attach(TransferMonitorPanel panel)
        {
            _panel = panel;
            _panel.Visibility = Visibility.Collapsed;
            _panel.HideRequested += (_, _) => Hide();
        }

        public void AttachOverlay(FrameworkElement placementTarget, ContentControl restingHost)
        {
            _placementTarget = placementTarget;
            _restingHost = restingHost;
        }

        public void Show(DependencyObject? context, string title)
        {
            if (_panel == null)
            {
                return;
            }

            RunOnUi(() =>
            {
                _sessionVersion++;
                _panel.SetSession(title);
                _panel.Visibility = Visibility.Visible;
                OpenOverlay();
            });
        }

        public void Update(ParallelTransferProgress? progress)
        {
            if (progress != null)
            {
                _panel?.Apply(progress);
            }
        }

        public void Finish(string summary)
        {
            RunOnUi(() =>
            {
                _panel?.Finish(summary);

                // Success: let the user see "Done" briefly, then dismiss the modal on its own.
                // Warnings/failures stay open until the user closes them.
                if (_overlayOpen && summary.StartsWith("Done", StringComparison.OrdinalIgnoreCase))
                {
                    var version = _sessionVersion;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        if (version == _sessionVersion)
                        {
                            Hide();
                        }
                    };
                    timer.Start();
                }
            });
        }

        public void Hide()
        {
            RunOnUi(() =>
            {
                if (_panel != null)
                {
                    _panel.Visibility = Visibility.Collapsed;
                }

                CloseOverlay(restoreVisible: false);
            });
        }

        private void OpenOverlay()
        {
            if (_panel == null || _placementTarget == null || _restingHost == null)
            {
                return;
            }

            if (_restingHost.Content == _panel)
            {
                _restingHost.Content = null;
            }

            EnsureOverlayWindow();
            if (_overlayWindow == null)
            {
                return;
            }

            _panel.Width = ModalWidth;
            _panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _panel.VerticalAlignment = VerticalAlignment.Center;
            _panel.Margin = new Thickness(24);
            _backdrop!.Children.Clear();
            _backdrop.Children.Add(_panel);
            _overlayOpen = true;
            PositionOverlayWindow();
            if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }

            _overlayWindow.Dispatcher.BeginInvoke(PositionOverlayWindow, System.Windows.Threading.DispatcherPriority.Loaded);
            SubscribeOverlayLayout();
        }

        private void CloseOverlay(bool restoreVisible)
        {
            UnsubscribeOverlayLayout();
            _overlayOpen = false;

            _backdrop?.Children.Clear();
            if (_overlayWindow != null)
            {
                if (_overlayWindow.IsVisible)
                {
                    _overlayWindow.Hide();
                }
            }

            if (_panel == null)
            {
                return;
            }

            _panel.Width = double.NaN;
            _panel.Margin = new Thickness(0);
            if (_restingHost != null && _restingHost.Content != _panel)
            {
                _restingHost.Content = _panel;
            }

            if (!restoreVisible)
            {
                _panel.Visibility = Visibility.Collapsed;
            }
        }

        private void EnsureOverlayWindow()
        {
            if (_overlayWindow != null || _placementTarget == null)
            {
                return;
            }

            var owner = Window.GetWindow(_placementTarget);
            _backdrop = new Grid { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x04, 0x08, 0x12)) };
            _overlayWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual,
                Topmost = false,
                Owner = owner,
                Content = _backdrop
            };
            _overlayWindow.Closing += OverlayWindow_Closing;
        }

        private void OverlayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void SubscribeOverlayLayout()
        {
            if (_placementTarget != null)
            {
                _placementTarget.SizeChanged -= OnOverlayLayoutChanged;
                _placementTarget.SizeChanged += OnOverlayLayoutChanged;
            }

            if (_panel != null)
            {
                _panel.SizeChanged -= OnOverlayLayoutChanged;
                _panel.SizeChanged += OnOverlayLayoutChanged;
            }

            var owner = _overlayWindow?.Owner;
            if (owner != null)
            {
                owner.LocationChanged -= OnOwnerLocationChanged;
                owner.LocationChanged += OnOwnerLocationChanged;
                owner.SizeChanged -= OnOverlayLayoutChanged;
                owner.SizeChanged += OnOverlayLayoutChanged;
            }
        }

        private void UnsubscribeOverlayLayout()
        {
            if (_placementTarget != null)
            {
                _placementTarget.SizeChanged -= OnOverlayLayoutChanged;
            }

            if (_panel != null)
            {
                _panel.SizeChanged -= OnOverlayLayoutChanged;
            }

            var owner = _overlayWindow?.Owner;
            if (owner != null)
            {
                owner.LocationChanged -= OnOwnerLocationChanged;
                owner.SizeChanged -= OnOverlayLayoutChanged;
            }
        }

        private void OnOwnerLocationChanged(object? sender, EventArgs e) => PositionOverlayWindow();

        private void OnOverlayLayoutChanged(object sender, SizeChangedEventArgs e) => PositionOverlayWindow();

        private void PositionOverlayWindow()
        {
            if (!_overlayOpen || _overlayWindow == null || _placementTarget == null || _panel == null)
            {
                return;
            }

            if (!_placementTarget.IsVisible || _placementTarget.ActualWidth < 8 || _placementTarget.ActualHeight < 8)
            {
                return;
            }

            var origin = _placementTarget.PointToScreen(new System.Windows.Point(0, 0));

            var source = PresentationSource.FromVisual(_placementTarget);
            if (source?.CompositionTarget != null)
            {
                origin = source.CompositionTarget.TransformFromDevice.Transform(origin);
            }

            _overlayWindow.Width = _placementTarget.ActualWidth;
            _overlayWindow.Height = _placementTarget.ActualHeight;
            _overlayWindow.Left = origin.X;
            _overlayWindow.Top = origin.Y;
        }

        private void RunOnUi(Action action)
        {
            var dispatcher = _panel?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }
    }
}
