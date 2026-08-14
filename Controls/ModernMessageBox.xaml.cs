using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using GitDeployPro.Services;
using GitDeployPro.Services.Localization;
using WpfSymbol = Wpf.Ui.Controls.SymbolRegular;

namespace GitDeployPro.Controls
{
    public partial class ModernMessageBox : Window
    {
        public bool Result { get; private set; }
        public MessageBoxResult MessageResult { get; private set; } = MessageBoxResult.None;

        private MessageBoxResult _primaryResult = MessageBoxResult.OK;
        private MessageBoxResult _secondaryResult = MessageBoxResult.None;
        private MessageBoxResult _cancelResult = MessageBoxResult.None;
        private bool _allowScrimDismiss;
        private bool _introPlayed;

        public ModernMessageBox(
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image,
            string? primaryText = null,
            string? secondaryText = null,
            string? cancelText = null)
        {
            InitializeComponent();
            LocalizationService.Instance.ApplyFlowDirection(this);

            TitleText.Text = title;
            MessageText.Text = message;
            ApplyKind(image, title);
            ApplyButtons(buttons, primaryText, secondaryText, cancelText);

            Loaded += ModernMessageBox_Loaded;
        }

        private void ModernMessageBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (_introPlayed)
            {
                return;
            }

            _introPlayed = true;
            CoverOwner();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                BeginFadeIn();
                OkButton.Focus();
            });
        }

        private void ApplyKind(MessageBoxImage image, string title)
        {
            var isSuccess = image == MessageBoxImage.Information
                            && title.Contains("Success", StringComparison.OrdinalIgnoreCase);

            string kindKey;
            string statusKey;
            string surfaceKey;
            WpfSymbol symbol;

            if (isSuccess)
            {
                kindKey = "dialog.kind.success";
                statusKey = "Status.Success";
                surfaceKey = "Status.SuccessSurface";
                symbol = WpfSymbol.CheckmarkCircle24;
            }
            else
            {
                switch (image)
                {
                    case MessageBoxImage.Error:
                        kindKey = "dialog.kind.error";
                        statusKey = "Status.Error";
                        surfaceKey = "Status.ErrorSurface";
                        symbol = WpfSymbol.ErrorCircle24;
                        OkButton.Background = TryBrush("Status.Error") ?? OkButton.Background;
                        OkButton.BorderBrush = OkButton.Background;
                        break;
                    case MessageBoxImage.Warning:
                        kindKey = "dialog.kind.warning";
                        statusKey = "Status.Warning";
                        surfaceKey = "Status.WarningSurface";
                        symbol = WpfSymbol.Warning24;
                        break;
                    case MessageBoxImage.Question:
                        kindKey = "dialog.kind.confirm";
                        statusKey = "Status.Info";
                        surfaceKey = "Status.InfoSurface";
                        symbol = WpfSymbol.QuestionCircle24;
                        break;
                    default:
                        kindKey = "dialog.kind.info";
                        statusKey = "Status.Info";
                        surfaceKey = "Status.InfoSurface";
                        symbol = WpfSymbol.Info24;
                        break;
                }
            }

            var status = TryBrush(statusKey);
            var surface = TryBrush(surfaceKey);
            if (status != null)
            {
                AccentBar.Background = status;
                KindLabel.Foreground = status;
                DialogIcon.Foreground = status;
            }

            if (surface != null)
            {
                IconBadge.Background = surface;
            }

            DialogIcon.Symbol = symbol;
            DialogIcon.Filled = false;
            KindLabel.Text = Loc.T(kindKey);
        }

        private void ApplyButtons(
            MessageBoxButton buttons,
            string? primaryText,
            string? secondaryText,
            string? cancelText)
        {
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    CancelButton.Visibility = Visibility.Collapsed;
                    ExtraButton.Visibility = Visibility.Collapsed;
                    DismissButton.Visibility = Visibility.Visible;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("common.ok") : primaryText;
                    OkButton.IsCancel = true;
                    _primaryResult = MessageBoxResult.OK;
                    _allowScrimDismiss = true;
                    break;
                case MessageBoxButton.YesNo:
                    CancelButton.Visibility = Visibility.Visible;
                    ExtraButton.Visibility = Visibility.Collapsed;
                    DismissButton.Visibility = Visibility.Collapsed;
                    CancelButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? Loc.T("common.no") : secondaryText;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("common.yes") : primaryText;
                    CancelButton.IsCancel = true;
                    _primaryResult = MessageBoxResult.Yes;
                    _cancelResult = MessageBoxResult.No;
                    break;
                case MessageBoxButton.OKCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    ExtraButton.Visibility = Visibility.Collapsed;
                    DismissButton.Visibility = Visibility.Collapsed;
                    CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("common.cancel") : cancelText;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("common.ok") : primaryText;
                    CancelButton.IsCancel = true;
                    _primaryResult = MessageBoxResult.OK;
                    _cancelResult = MessageBoxResult.Cancel;
                    break;
                case MessageBoxButton.YesNoCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    ExtraButton.Visibility = Visibility.Visible;
                    DismissButton.Visibility = Visibility.Collapsed;
                    OkButton.Content = string.IsNullOrWhiteSpace(primaryText) ? Loc.T("common.yes") : primaryText;
                    ExtraButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? Loc.T("common.no") : secondaryText;
                    CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("common.cancel") : cancelText;
                    CancelButton.IsCancel = true;
                    _primaryResult = MessageBoxResult.Yes;
                    _secondaryResult = MessageBoxResult.No;
                    _cancelResult = MessageBoxResult.Cancel;
                    break;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Complete(_primaryResult);

        private void ExtraButton_Click(object sender, RoutedEventArgs e)
            => Complete(_secondaryResult == MessageBoxResult.None ? MessageBoxResult.No : _secondaryResult);

        private void CancelButton_Click(object sender, RoutedEventArgs e)
            => Complete(_cancelResult == MessageBoxResult.None ? MessageBoxResult.Cancel : _cancelResult);

        private void DismissButton_Click(object sender, RoutedEventArgs e) => Complete(_primaryResult);

        private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_allowScrimDismiss && e.OriginalSource == ScrimGrid)
            {
                Complete(_primaryResult);
            }
        }

        private void DialogCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            if (CancelButton.Visibility == Visibility.Visible)
            {
                Complete(_cancelResult == MessageBoxResult.None ? MessageBoxResult.Cancel : _cancelResult);
            }
            else
            {
                Complete(_primaryResult);
            }

            e.Handled = true;
        }

        private void Complete(MessageBoxResult result)
        {
            MessageResult = result;
            Result = result == MessageBoxResult.OK || result == MessageBoxResult.Yes;
            Close();
        }

        private void CoverOwner()
        {
            var owner = Owner;
            WindowOwnerService.RestoreIfMinimized(owner);
            if (!TryCoverVisibleOwner())
            {
                PlaceOnScreenFallback();
            }
        }

        private bool TryCoverVisibleOwner()
        {
            var owner = Owner;
            if (owner == null
                || !owner.IsVisible
                || owner.WindowState == WindowState.Minimized
                || owner.ActualWidth < 80
                || owner.ActualHeight < 80)
            {
                return false;
            }

            try
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                var origin = owner.PointToScreen(new System.Windows.Point(0, 0));
                if (origin.X <= -10000 || origin.Y <= -10000)
                {
                    return false;
                }

                var source = PresentationSource.FromVisual(owner);
                if (source?.CompositionTarget != null)
                {
                    var dip = source.CompositionTarget.TransformFromDevice.Transform(origin);
                    Left = dip.X;
                    Top = dip.Y;
                }

                if (!WindowOwnerService.IsOnScreen(Left, Top))
                {
                    return false;
                }

                Width = Math.Max(owner.ActualWidth, 420);
                Height = Math.Max(owner.ActualHeight, 260);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void PlaceOnScreenFallback()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 520;
            Height = 320;
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            ShowInTaskbar = true;
        }

        private void BeginFadeIn()
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;

            DialogCard.BeginAnimation(UIElement.OpacityProperty, null);
            DialogCard.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            DialogCard.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private System.Windows.Media.Brush? TryBrush(string resourceKey)
        {
            return TryFindResource(resourceKey) as System.Windows.Media.Brush
                   ?? System.Windows.Application.Current?.TryFindResource(resourceKey) as System.Windows.Media.Brush;
        }

        private static void PrepareOwnerAndPlacement(ModernMessageBox msgBox, Window? owner, DependencyObject? context)
        {
            var resolvedOwner = WindowOwnerService.ResolveOwner(context, owner) ?? System.Windows.Application.Current?.MainWindow;
            WindowOwnerService.RestoreIfMinimized(resolvedOwner);
            if (resolvedOwner != null && !ReferenceEquals(msgBox, resolvedOwner))
            {
                WindowOwnerService.ApplyOwner(msgBox, resolvedOwner, centerOnOwner: false);
                msgBox.WindowStartupLocation = WindowStartupLocation.Manual;
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
