using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDeployPro.Services.Localization;
using GitDeployPro.Services.Modem;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Button = System.Windows.Controls.Button;

namespace GitDeployPro.Pages
{
    public sealed class SmsLink
    {
        public string Url { get; init; } = "";
        public string Display { get; init; } = "";
    }

    public sealed class SmsItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _applied;

        public event PropertyChangedEventHandler? PropertyChanged;
        public Action? SelectionChanged { get; set; }

        public ModemSms Sms { get; }
        public SmsKind Kind { get; }
        public PackageInfo? Package { get; }
        public double UsagePercent { get; }

        public SmsItem(ModemSms sms, bool applied)
        {
            Sms = sms;
            Kind = OperatorSmsParser.Classify(sms.Text);
            if (Kind == SmsKind.Package && OperatorSmsParser.TryParsePackage(sms.Text, out var pkg))
            {
                Package = pkg;
            }

            if (Kind == SmsKind.Usage && OperatorSmsParser.TryParseUsagePercent(sms.Text, out var pct))
            {
                UsagePercent = pct;
            }

            _applied = applied;
            Links = OperatorSmsParser.ExtractLinks(sms.Text)
                .Select(u => new SmsLink { Url = u, Display = u.Replace("https://", "").Replace("http://", "").TrimEnd('/') })
                .ToList();
        }

        public string Id => Sms.Id;
        public string From => string.IsNullOrWhiteSpace(Sms.From) ? "?" : Sms.From;
        public string Text => Sms.Text;
        public IReadOnlyList<SmsLink> Links { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                SelectionChanged?.Invoke();
            }
        }

        public string Initial => From.Trim().Length > 0 ? char.ToUpperInvariant(From.Trim()[0]).ToString() : "?";

        public string DateText
        {
            get
            {
                var d = Sms.Date;
                var time = d.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (d.Date == DateTime.Today)
                {
                    return Loc.T("sms.today") + " · " + time;
                }

                if (d.Date == DateTime.Today.AddDays(-1))
                {
                    return Loc.T("sms.yesterday") + " · " + time;
                }

                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + time;
            }
        }

        public FlowDirection TextFlow =>
            Sms.Text.Any(c => c >= '؀' && c <= 'ۿ') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public string KindLabel => Kind switch
        {
            SmsKind.Usage => Loc.T("sms.kind.usage"),
            SmsKind.Package => Loc.T("sms.kind.package"),
            SmsKind.Promo => Loc.T("sms.kind.promo"),
            _ => Loc.T("sms.kind.other")
        };

        private static Brush Res(string key, Brush fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;

        public Brush KindBrush => Kind switch
        {
            SmsKind.Usage => UsagePercent >= 90 ? Res("Status.Error", Brushes.IndianRed) : Res("Status.Warning", Brushes.Orange),
            SmsKind.Package => Res("Status.Success", Brushes.SeaGreen),
            SmsKind.Promo => Res("Status.Info", Brushes.SteelBlue),
            _ => Res("Text.Muted", Brushes.Gray)
        };

        public Brush KindSurface => Kind switch
        {
            SmsKind.Usage => UsagePercent >= 90 ? Res("Status.ErrorSurface", Brushes.Transparent) : Res("Status.WarningSurface", Brushes.Transparent),
            SmsKind.Package => Res("Status.SuccessSurface", Brushes.Transparent),
            SmsKind.Promo => Res("Status.InfoSurface", Brushes.Transparent),
            _ => Res("Surface.Input", Brushes.Transparent)
        };

        public Visibility UnreadVisibility => Sms.Unread ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PackageVisibility => Package != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UsageVisibility => Kind == SmsKind.Usage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinksVisibility => Links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PackageDaysVisibility => Package is { Days: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PackageExpiryVisibility => Package is { Expiry.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        public string PackageGbText => Package == null ? "" : Loc.T("sms.packageGb", Package.Gb.ToString("0.##", CultureInfo.InvariantCulture));
        public string PackageDaysText => Package == null ? "" : Loc.T("sms.packageDays", Package.Days);
        public string PackageExpiryText => Package == null ? "" : Loc.T("sms.packageExpiry", Package.Expiry);
        public string UsagePercentText => UsagePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

        public bool CanAddToPlan => Package != null && !_applied;

        public string AddToPlanLabel => Package == null
            ? ""
            : _applied
                ? Loc.T("sms.addedToPlan")
                : Loc.T("sms.addToPlan", Package.Gb.ToString("0.##", CultureInfo.InvariantCulture));

        public void MarkApplied()
        {
            _applied = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddToPlan)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddToPlanLabel)));
        }
    }

    public partial class ModemSmsPage : Page
    {
        private readonly ModemUsageMonitorService _monitor = ModemUsageMonitorService.Instance;
        private ModemSmsBox _box = ModemSmsBox.Inbox;
        private int _page = 1;
        private int _pageSize = 20;
        private int _total;
        private bool _busy;
        private bool _loaded;
        private List<ModemSms>? _everything; // filled by "select everything"
        private List<SmsItem> _items = new();

        public ModemSmsPage()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (_loaded)
                {
                    return;
                }

                _loaded = true;
                await LoadAsync();
            };
        }

        // ---- loading ------------------------------------------------------------------------

        private async Task LoadAsync()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            LoadingBar.Visibility = Visibility.Visible;
            ErrorBanner.Visibility = Visibility.Collapsed;
            try
            {
                var conn = _monitor.BuildConnection();
                var result = await Task.Run(() => _monitor.Driver.GetSmsPageAsync(conn, _box, _page, _pageSize, default));
                _total = result.Total;
                var pages = Math.Max(1, (int)Math.Ceiling(_total / (double)_pageSize));
                if (_page > pages)
                {
                    _page = pages;
                }

                _items = result.Items.Select(m =>
                {
                    var item = new SmsItem(m, IsApplied(m));
                    item.SelectionChanged = UpdateSelectionUi;
                    return item;
                }).ToList();

                if (_box == ModemSmsBox.Inbox)
                {
                    InboxCount.Text = _total.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    SentCount.Text = _total.ToString(CultureInfo.InvariantCulture);
                }

                SubtitleText.Text = Loc.T("sms.subtitle", _total);
            }
            catch (Exception ex)
            {
                _items = new List<SmsItem>();
                ErrorText.Text = ex.Message;
                ErrorBanner.Visibility = Visibility.Visible;
            }
            finally
            {
                _busy = false;
                LoadingBar.Visibility = Visibility.Collapsed;
            }

            ApplyFilter();
            BuildPager();
        }

        private bool IsApplied(ModemSms m) =>
            OperatorSmsParser.TryParsePackage(m.Text, out var pkg) && _monitor.IsPackageApplied(m, pkg);

        private void ApplyFilter()
        {
            var q = (SearchBox?.Text ?? "").Trim();
            IEnumerable<SmsItem> view = _items;
            if (q.Length > 0)
            {
                view = _items.Where(i =>
                    i.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.From.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    OperatorSmsParser.Normalize(i.Text).Contains(OperatorSmsParser.Normalize(q), StringComparison.OrdinalIgnoreCase));
            }

            var list = view.ToList();
            MessageList.ItemsSource = list;
            EmptyState.Visibility = list.Count == 0 && ErrorBanner.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.ScrollToTop();
            UpdateSelectionUi();
        }

        // ---- selection ----------------------------------------------------------------------

        private bool AllSelectedMode => _everything != null && _everything.Count > 0;

        private int SelectedCount => AllSelectedMode ? _everything!.Count : _items.Count(i => i.IsChecked);

        private void UpdateSelectionUi()
        {
            var visible = (MessageList.ItemsSource as IEnumerable<SmsItem>)?.ToList() ?? new List<SmsItem>();
            var checkedCount = visible.Count(i => i.IsChecked);
            SelectAllCheck.IsChecked = visible.Count > 0 && checkedCount == visible.Count ? true : checkedCount == 0 ? false : (bool?)null;

            var n = SelectedCount;
            DeleteButton.IsEnabled = n > 0;
            DeleteLabel.Text = Loc.T("sms.delete", n);

            var canEverything = _total > _items.Count || AllSelectedMode;
            SelectEverythingButton.Visibility = canEverything && _total > 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectEverythingButton.Content = AllSelectedMode ? Loc.T("sms.clearSelection") : Loc.T("sms.selectEverything", _total);

            AllSelectedBanner.Visibility = AllSelectedMode ? Visibility.Visible : Visibility.Collapsed;
            AllSelectedText.Text = Loc.T("sms.allSelected", _everything?.Count ?? 0);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            var on = SelectAllCheck.IsChecked == true;
            _everything = null;
            foreach (var i in (MessageList.ItemsSource as IEnumerable<SmsItem>) ?? Enumerable.Empty<SmsItem>())
            {
                i.IsChecked = on;
            }

            UpdateSelectionUi();
        }

        private async void SelectEverything_Click(object sender, RoutedEventArgs e)
        {
            if (AllSelectedMode)
            {
                _everything = null;
                foreach (var i in _items)
                {
                    i.IsChecked = false;
                }

                UpdateSelectionUi();
                return;
            }

            if (_busy)
            {
                return;
            }

            _busy = true;
            LoadingBar.Visibility = Visibility.Visible;
            try
            {
                var conn = _monitor.BuildConnection();
                var driver = _monitor.Driver;
                var all = new List<ModemSms>();
                for (var p = 1; all.Count < _total && p <= 200; p++)
                {
                    var box = _box;
                    var page = await Task.Run(() => driver.GetSmsPageAsync(conn, box, p, 50, default));
                    if (page.Items.Count == 0)
                    {
                        break;
                    }

                    all.AddRange(page.Items);
                }

                _everything = all;
                foreach (var i in _items)
                {
                    i.IsChecked = true;
                }
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
                ErrorBanner.Visibility = Visibility.Visible;
            }
            finally
            {
                _busy = false;
                LoadingBar.Visibility = Visibility.Collapsed;
            }

            UpdateSelectionUi();
        }

        // ---- delete -------------------------------------------------------------------------

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var ids = AllSelectedMode
                ? _everything!.Select(m => m.Id).ToList()
                : _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
            await DeleteAsync(ids, AllSelectedMode);
        }

        private async void DeleteOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SmsItem item)
            {
                await DeleteAsync(new List<string> { item.Id }, false);
            }
        }

        private async Task DeleteAsync(List<string> ids, bool everything)
        {
            if (ids.Count == 0 || _busy)
            {
                return;
            }

            var text = everything ? Loc.T("sms.confirmDeleteAll", ids.Count) : Loc.T("sms.confirmDelete", ids.Count);
            if (System.Windows.MessageBox.Show(text, Loc.T("sms.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _busy = true;
            LoadingBar.Visibility = Visibility.Visible;
            try
            {
                var conn = _monitor.BuildConnection();
                var driver = _monitor.Driver;
                // Modem deletes are most reliable in small batches.
                for (var i = 0; i < ids.Count; i += 20)
                {
                    var batch = ids.Skip(i).Take(20).ToList();
                    await Task.Run(() => driver.DeleteSmsAsync(conn, batch, default));
                }

                _everything = null;
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
                ErrorBanner.Visibility = Visibility.Visible;
            }
            finally
            {
                _busy = false;
                LoadingBar.Visibility = Visibility.Collapsed;
            }

            await LoadAsync();
        }

        // ---- per message actions ------------------------------------------------------------

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SmsItem item)
            {
                try
                {
                    System.Windows.Clipboard.SetText(item.Text);
                }
                catch
                {
                    // clipboard busy
                }
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                    // no default browser
                }
            }
        }

        private void AddToPlan_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SmsItem item && item.Package != null)
            {
                _monitor.TryApplyPackage(item.Sms, item.Package, out _);
                item.MarkApplied();
                ModemUsageMonitorService.Instance.StartFromConfig();
            }
        }

        // ---- tabs / paging ------------------------------------------------------------------

        private async void InboxTab_Click(object sender, RoutedEventArgs e) => await SwitchBoxAsync(ModemSmsBox.Inbox);

        private async void SentTab_Click(object sender, RoutedEventArgs e) => await SwitchBoxAsync(ModemSmsBox.Sent);

        private async Task SwitchBoxAsync(ModemSmsBox box)
        {
            if (_box == box || _busy)
            {
                return;
            }

            _box = box;
            _page = 1;
            _everything = null;
            InboxTab.Style = (Style)FindResource(box == ModemSmsBox.Inbox ? "Sms.Tab.Active" : "Sms.Tab");
            SentTab.Style = (Style)FindResource(box == ModemSmsBox.Sent ? "Sms.Tab.Active" : "Sms.Tab");
            await LoadAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _everything = null;
            await LoadAsync();
        }

        private async void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || PageSizeCombo.SelectedItem is not ComboBoxItem item ||
                !int.TryParse(item.Content?.ToString(), out var size) || size == _pageSize)
            {
                return;
            }

            _pageSize = size;
            _page = 1;
            _everything = null;
            await LoadAsync();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
            {
                ApplyFilter();
            }
        }

        private async Task GoToPageAsync(int page)
        {
            if (_busy)
            {
                return;
            }

            _page = Math.Max(1, page);
            _everything = null;
            await LoadAsync();
        }

        private void BuildPager()
        {
            PagerPanel.Children.Clear();
            var pages = Math.Max(1, (int)Math.Ceiling(_total / (double)_pageSize));
            var from = _total == 0 ? 0 : (_page - 1) * _pageSize + 1;
            var to = Math.Min(_total, _page * _pageSize);
            RangeText.Text = Loc.T("sms.range", from, to, _total);

            Button Make(string label, int target, bool active = false, bool enabled = true, string? tip = null)
            {
                var b = new Button
                {
                    Content = label,
                    Style = (Style)FindResource(active ? "Sms.PageBtn.Active" : "Sms.PageBtn"),
                    IsEnabled = enabled,
                    ToolTip = tip
                };
                b.Click += async (_, _) => await GoToPageAsync(target);
                return b;
            }

            PagerPanel.Children.Add(Make("«", 1, enabled: _page > 1, tip: Loc.T("sms.first")));
            PagerPanel.Children.Add(Make("‹", _page - 1, enabled: _page > 1, tip: Loc.T("sms.prev")));

            var shown = new SortedSet<int> { 1, pages, _page - 1, _page, _page + 1 };
            var last = 0;
            foreach (var p in shown.Where(p => p >= 1 && p <= pages))
            {
                if (p - last > 1)
                {
                    PagerPanel.Children.Add(new TextBlock
                    {
                        Text = "…",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 0),
                        Foreground = (Brush)FindResource("Text.Muted")
                    });
                }

                PagerPanel.Children.Add(Make(p.ToString(CultureInfo.InvariantCulture), p, active: p == _page));
                last = p;
            }

            PagerPanel.Children.Add(Make("›", _page + 1, enabled: _page < pages, tip: Loc.T("sms.next")));
            PagerPanel.Children.Add(Make("»", pages, enabled: _page < pages, tip: Loc.T("sms.last")));
        }

        // ---- compose ------------------------------------------------------------------------

        private void Compose_Click(object sender, RoutedEventArgs e)
        {
            ComposePanel.Visibility = ComposePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (ComposePanel.Visibility == Visibility.Visible)
            {
                ComposePhone.Focus();
            }
        }

        private void CancelCompose_Click(object sender, RoutedEventArgs e)
        {
            ComposePanel.Visibility = Visibility.Collapsed;
        }

        private void ComposeText_TextChanged(object sender, TextChangedEventArgs e)
        {
            var t = ComposeText.Text ?? "";
            var unicode = t.Any(c => c > 127);
            var single = unicode ? 70 : 160;
            var multi = unicode ? 67 : 153;
            var parts = t.Length == 0 ? 0 : t.Length <= single ? 1 : (int)Math.Ceiling(t.Length / (double)multi);
            ComposeCounter.Text = Loc.T("sms.counter", t.Length, parts);
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var phone = (ComposePhone.Text ?? "").Trim();
            var text = ComposeText.Text ?? "";
            if (phone.Length == 0 || text.Trim().Length == 0)
            {
                ComposeStatus.Text = Loc.T("sms.needPhoneText");
                return;
            }

            SendButton.IsEnabled = false;
            ComposeStatus.Text = Loc.T("sms.sending");
            try
            {
                var conn = _monitor.BuildConnection();
                var normalized = OperatorSmsParser.Normalize(phone);
                await Task.Run(() => _monitor.Driver.SendSmsAsync(conn, normalized, text, default));
                ComposeStatus.Text = Loc.T("sms.sentOk");
                ComposeText.Text = string.Empty;
                if (_box == ModemSmsBox.Sent)
                {
                    await Task.Delay(1500);
                    await LoadAsync();
                }
            }
            catch (Exception ex)
            {
                ComposeStatus.Text = ex.Message;
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true)
            {
                NavigationService.GoBack();
            }
        }
    }
}
