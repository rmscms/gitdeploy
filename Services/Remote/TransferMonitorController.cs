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
                CloseOverlay(restoreVisible: true);
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

            _overlayWindow.Content = _panel;
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

            if (_overlayWindow != null)
            {
                _overlayWindow.Content = null;
                if (_overlayWindow.IsVisible)
                {
                    _overlayWindow.Hide();
                }
            }

            if (_panel == null)
            {
                return;
            }

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
            _overlayWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Topmost = false,
                Owner = owner,
                Content = _panel
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

            var width = _panel.ActualWidth > 1 ? _panel.ActualWidth : (_panel.Width > 1 ? _panel.Width : 360);
            var height = _panel.ActualHeight > 1 ? _panel.ActualHeight : 180;
            var origin = _placementTarget.PointToScreen(new System.Windows.Point(
                Math.Max(8, _placementTarget.ActualWidth - width - 12),
                Math.Max(8, _placementTarget.ActualHeight - height - 12)));

            var source = PresentationSource.FromVisual(_placementTarget);
            if (source?.CompositionTarget != null)
            {
                origin = source.CompositionTarget.TransformFromDevice.Transform(origin);
            }

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
