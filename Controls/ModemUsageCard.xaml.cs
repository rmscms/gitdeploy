using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using GitDeployPro.Pages;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Modem;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace GitDeployPro.Controls
{
    public partial class ModemUsageCard : UserControl
    {
        private const double GaugeCx = 85, GaugeCy = 82, GaugeR = 64, GaugeStart = 135, GaugeSweep = 270;

        public ModemUsageCard()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                ModemUsageMonitorService.Instance.SnapshotChanged += Monitor_SnapshotChanged;
                Render(ModemUsageMonitorService.Instance.Latest);
                if (ModemUsageMonitorService.Instance.Latest.Enabled)
                {
                    _ = RefreshAsync();
                }
            };
            Unloaded += (_, _) => ModemUsageMonitorService.Instance.SnapshotChanged -= Monitor_SnapshotChanged;
        }

        private void Monitor_SnapshotChanged(object? sender, ModemSnapshot e) =>
            Dispatcher.BeginInvoke(new Action(() => Render(e)));

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            try
            {
                RefreshButton.IsEnabled = false;
                await ModemUsageMonitorService.Instance.PollNowAsync().ConfigureAwait(true);
            }
            catch
            {
                // surfaced through snapshot.Error
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new GitDeployPro.Services.ConfigurationService().LoadGlobalConfig();
            if (!cfg.ModemEnabled)
            {
                OpenSettings();
                return;
            }

            await RefreshAsync();
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ModemUsageMonitorService.Instance.Latest.Enabled)
            {
                OpenSettings();
                return;
            }

            if (System.Windows.MessageBox.Show(Loc.T("modem.card.resetConfirm"), Loc.T("modem.card.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await ModemUsageMonitorService.Instance.ResetUsageAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, Loc.T("modem.card.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetupButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void MessagesButton_Click(object sender, RoutedEventArgs e)
        {
            DependencyObject? d = this;
            while (d != null && d is not Page)
            {
                d = VisualTreeHelper.GetParent(d);
            }

            (d as Page)?.NavigationService?.Navigate(new ModemSmsPage());
        }

        private void OpenSettings()
        {
            DependencyObject? d = this;
            while (d != null && d is not Page)
            {
                d = VisualTreeHelper.GetParent(d);
            }

            (d as Page)?.NavigationService?.Navigate(new SettingsPage());
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
            Render(ModemUsageMonitorService.Instance.Latest);

        // ---------------------------------------------------------------------------------

        private static Brush B(string key, Brush fallback)
        {
            return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;
        }

        private static Brush LevelBrush(double percent) =>
            percent >= 90 ? B("Status.Error", Brushes.IndianRed)
            : percent >= 70 ? B("Status.Warning", Brushes.Orange)
            : B("Status.Success", Brushes.SeaGreen);

        private void Render(ModemSnapshot s)
        {
            if (!s.Enabled)
            {
                EmptyPanel.Visibility = Visibility.Visible;
                DataPanel.Visibility = Visibility.Collapsed;
                StatusBadge.Visibility = Visibility.Collapsed;
                SubtitleText.Text = string.Empty;
                return;
            }

            EmptyPanel.Visibility = Visibility.Collapsed;
            DataPanel.Visibility = Visibility.Visible;
            SubtitleText.Text = s.DriverName;

            var offline = !string.IsNullOrEmpty(s.Error);
            OfflineBanner.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
            OfflineText.Text = offline ? Loc.T("modem.card.offline", s.Error) : string.Empty;

            if (!s.HasData)
            {
                StatusBadge.Visibility = Visibility.Collapsed;
                GaugeCanvas.Children.Clear();
                GaugePercentText.Text = "—";
                GaugeCaptionText.Text = string.Empty;
                UsedText.Text = RemainingText.Text = DownText.Text = UpText.Text = "—";
                ForecastText.Text = string.Empty;
                RateText.Text = string.Empty;
                BarsCanvas.Children.Clear();
                SparkCanvas.Children.Clear();
                UpdatedText.Text = string.Empty;
                SmsText.Text = string.Empty;
                return;
            }

            var fmt = ModemUsageMonitorService.FormatBytes;
            var hasPlan = s.Percent >= 0;
            var level = hasPlan ? LevelBrush(s.Percent) : B("Accent.Primary", Brushes.SteelBlue);

            DrawGauge(hasPlan ? s.Percent : 0, level);
            GaugePercentText.Text = hasPlan ? s.Percent.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            GaugeCaptionText.Text = hasPlan ? Loc.T("modem.card.plan") + " " + fmt(s.PlanBytes) : string.Empty;
            UsedText.Text = fmt(s.UsedBytes);
            RemainingText.Text = hasPlan ? fmt(s.RemainingBytes) : "—";
            RemainingText.Foreground = level;
            DownText.Text = fmt(s.DownloadBytes);
            UpText.Text = fmt(s.UploadBytes);

            if (hasPlan)
            {
                StatusBadge.Visibility = Visibility.Visible;
                StatusBadge.Style = (Style)FindResource(
                    s.Percent >= 90 ? "App.StatusBadge.Error"
                    : s.Percent >= 70 ? "App.StatusBadge.Warning"
                    : "App.StatusBadge.Success");
                StatusBadgeText.Text = s.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                StatusBadgeText.Foreground = level;
            }
            else
            {
                StatusBadge.Visibility = Visibility.Collapsed;
            }

            ForecastText.Text = !hasPlan ? Loc.T("modem.card.noPlan")
                : s.Percent >= 100 ? Loc.T("modem.card.daysLeftNow")
                : s.DaysLeft >= 0 ? Loc.T("modem.card.daysLeft", s.DaysLeft.ToString("0.#", CultureInfo.InvariantCulture))
                : string.Empty;
            ForecastText.Foreground = hasPlan && s.DaysLeft >= 0 && s.DaysLeft < 3
                ? B("Status.Warning", Brushes.Orange)
                : B("Text.Muted", Brushes.Gray);

            RateText.Text = "↓ " + fmt(s.DownRate) + "/s  ↑ " + fmt(s.UpRate) + "/s";
            DrawBars(s);
            DrawSpark(s);
            if (!string.IsNullOrEmpty(s.SmsError))
            {
                SmsText.Text = Loc.T("modem.card.smsError", s.SmsError);
            }
            else
            {
                SmsText.Text = s.OperatorPercent >= 0
                    ? Loc.T("modem.card.operatorSms", s.OperatorPercent.ToString("0.#", CultureInfo.InvariantCulture), s.OperatorSmsDate.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))
                      + (s.OperatorRemainingGb >= 0 ? " · " + Loc.T("modem.card.operatorLeft", s.OperatorRemainingGb.ToString("0.#", CultureInfo.InvariantCulture)) : "")
                    : string.Empty;
            }

            UpdatedText.Text = Loc.T("modem.card.updated", s.SampledLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }

        private static Point Polar(double angleDeg, double r)
        {
            var a = angleDeg * Math.PI / 180.0;
            return new Point(GaugeCx + r * Math.Cos(a), GaugeCy + r * Math.Sin(a));
        }

        private static Path Arc(double startDeg, double sweepDeg, Brush stroke)
        {
            var end = startDeg + sweepDeg;
            var fig = new PathFigure { StartPoint = Polar(startDeg, GaugeR), IsClosed = false };
            fig.Segments.Add(new ArcSegment(
                Polar(end, GaugeR),
                new Size(GaugeR, GaugeR),
                0,
                sweepDeg > 180,
                SweepDirection.Clockwise,
                true));
            return new Path
            {
                Data = new PathGeometry(new[] { fig }),
                Stroke = stroke,
                StrokeThickness = 13,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        private void DrawGauge(double percent, Brush level)
        {
            GaugeCanvas.Children.Clear();
            GaugeCanvas.Children.Add(Arc(GaugeStart, GaugeSweep, B("Border.Subtle", Brushes.DimGray)));
            var sweep = Math.Clamp(percent, 0, 100) / 100.0 * GaugeSweep;
            if (sweep > 1)
            {
                GaugeCanvas.Children.Add(Arc(GaugeStart, sweep, level));
            }

            // Threshold ticks (70/90) like a fuel gauge.
            foreach (var t in new[] { 70, 90 })
            {
                var ang = GaugeStart + t / 100.0 * GaugeSweep;
                var p1 = Polar(ang, GaugeR - 11);
                var p2 = Polar(ang, GaugeR + 11);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Stroke = B("Surface.Base", Brushes.Black),
                    StrokeThickness = 2
                });
            }
        }

        private void DrawBars(ModemSnapshot s)
        {
            BarsCanvas.Children.Clear();
            var w = BarsCanvas.ActualWidth;
            var h = BarsCanvas.ActualHeight > 0 ? BarsCanvas.ActualHeight : 64;
            if (w <= 0 || s.Days.Count == 0)
            {
                return;
            }

            var max = Math.Max(1, s.Days.Max(d => d.Bytes));
            var slot = w / s.Days.Count;
            var barW = Math.Max(3, slot - 4);
            for (var i = 0; i < s.Days.Count; i++)
            {
                var d = s.Days[i];
                var bh = d.Bytes <= 0 ? 2 : Math.Max(3, d.Bytes / (double)max * (h - 4));
                var isToday = i == s.Days.Count - 1;
                var rect = new Rectangle
                {
                    Width = barW,
                    Height = bh,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = isToday ? B("Accent.Primary", Brushes.SteelBlue) : B("Accent.Soft", Brushes.SlateGray),
                    Stroke = isToday ? null : B("Accent.Primary", Brushes.SteelBlue),
                    StrokeThickness = isToday ? 0 : 1,
                    ToolTip = d.Date.ToString("MMM dd", CultureInfo.InvariantCulture) + " — " + ModemUsageMonitorService.FormatBytes(d.Bytes)
                };
                Canvas.SetLeft(rect, i * slot + 2);
                Canvas.SetTop(rect, h - bh);
                BarsCanvas.Children.Add(rect);
            }
        }

        private void DrawSpark(ModemSnapshot s)
        {
            SparkCanvas.Children.Clear();
            var w = SparkCanvas.ActualWidth;
            var h = SparkCanvas.ActualHeight > 0 ? SparkCanvas.ActualHeight : 64;
            if (w <= 0 || s.Rates.Count < 2)
            {
                return;
            }

            var max = Math.Max(1, s.Rates.Max(r => Math.Max(r.Down, r.Up)));
            PointCollection Points(Func<ModemRatePoint, long> sel)
            {
                var pc = new PointCollection();
                for (var i = 0; i < s.Rates.Count; i++)
                {
                    var x = i * (w - 2) / (s.Rates.Count - 1) + 1;
                    var y = h - 2 - sel(s.Rates[i]) / (double)max * (h - 6);
                    pc.Add(new Point(x, y));
                }

                return pc;
            }

            SparkCanvas.Children.Add(new Polyline
            {
                Points = Points(r => r.Down),
                Stroke = B("Status.Info", Brushes.DodgerBlue),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round
            });
            SparkCanvas.Children.Add(new Polyline
            {
                Points = Points(r => r.Up),
                Stroke = B("Accent.Primary", Brushes.SteelBlue),
                StrokeThickness = 1.4,
                Opacity = 0.8,
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }
}
