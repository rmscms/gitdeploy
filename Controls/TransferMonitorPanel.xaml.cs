using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;

namespace GitDeployPro.Controls
{
    public partial class TransferMonitorPanel : System.Windows.Controls.UserControl
    {
        private readonly StringBuilder _log = new();
        private readonly ObservableCollection<ParallelWorkerSnapshot> _workers = new();
        private long _lastSequence;

        public event EventHandler? HideRequested;

        public TransferMonitorPanel()
        {
            InitializeComponent();
            WorkerList.ItemsSource = _workers;
        }

        public void SetSession(string title)
        {
            RunOnUi(() =>
            {
                SessionTitle.Text = title;
                HideButton.Content = "Hide";
                FooterText.Text = "Hide only closes this window. Transfer keeps running.";
                HeadlineText.Foreground = LogColorizer.Resolve("Status.Info", "#5BB8FF");
                _log.Clear();
                LogText.Inlines.Clear();
                _lastSequence = 0;
            });
        }

        public void Apply(ParallelTransferProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            RunOnUi(() =>
            {
                HeadlineText.Text = progress.TotalBytes > 0
                    ? $"{progress.ActiveWorkers}/{progress.RequestedWorkers} workers · {progress.Completed}/{progress.Total} files · {progress.Phase}"
                    : (string.IsNullOrWhiteSpace(progress.Headline) ? progress.Phase : progress.Headline);
                OverallBar.IsIndeterminate = progress.IsIndeterminate;
                OverallBar.Value = Math.Clamp(progress.Percent, 0, 100);
                PercentText.Text = progress.TotalBytes > 0
                    ? $"{FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)} ({progress.Percent:0.1}%)"
                    : (progress.IsIndeterminate
                        ? progress.Phase
                        : $"{progress.Percent:0}% · {progress.Completed}/{progress.Total} files");

                _workers.Clear();
                foreach (var worker in progress.Workers)
                {
                    _workers.Add(worker);
                }

                if (progress.Sequence > _lastSequence && !string.IsNullOrWhiteSpace(progress.LastLine))
                {
                    _lastSequence = progress.Sequence;
                    AppendLog(progress.LastLine);
                }
            });
        }

        public void Finish(string summary)
        {
            RunOnUi(() =>
            {
                HeadlineText.Text = summary;
                HeadlineText.Foreground = LogColorizer.PickBrush(summary);
                OverallBar.IsIndeterminate = false;
                HideButton.Content = "Close";
                AppendLog(summary);
            });
        }

        private void AppendLog(string line)
        {
            var timestamp = AppTimeService.LocalNow.ToString("HH:mm:ss");
            if (LogText.Inlines.Count > 0)
            {
                LogText.Inlines.Add(new System.Windows.Documents.LineBreak());
            }

            LogText.Inlines.AddRange(LogColorizer.BuildRuns(timestamp, line));
            LogScroll.ScrollToEnd();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RunOnUi(Action action)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(action);
                return;
            }

            action();
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            var index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }

            return $"{value:0.##} {units[index]}";
        }
    }
}
