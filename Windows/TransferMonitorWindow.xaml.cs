using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using GitDeployPro.Models;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class TransferMonitorWindow : MetroWindow
    {
        private readonly StringBuilder _log = new();
        private readonly ObservableCollection<ParallelWorkerSnapshot> _workers = new();
        private long _lastSequence;

        public TransferMonitorWindow()
        {
            InitializeComponent();
            WorkerList.ItemsSource = _workers;
        }

        public void SetSession(string title)
        {
            Dispatcher.Invoke(() =>
            {
                Title = title;
                SessionTitle.Text = title;
            });
        }

        public void Apply(ParallelTransferProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                HeadlineText.Text = string.IsNullOrWhiteSpace(progress.Headline)
                    ? progress.Phase
                    : progress.Headline;
                OverallBar.IsIndeterminate = progress.IsIndeterminate;
                OverallBar.Value = Math.Clamp(progress.Percent, 0, 100);
                PercentText.Text = progress.IsIndeterminate
                    ? progress.Phase
                    : $"{progress.Percent:0}% · {progress.Completed}/{progress.Total} files · {progress.ActiveWorkers}/{progress.RequestedWorkers} live";

                _workers.Clear();
                foreach (var worker in progress.Workers)
                {
                    _workers.Add(worker);
                }

                if (progress.Sequence > _lastSequence && !string.IsNullOrWhiteSpace(progress.LastLine))
                {
                    _lastSequence = progress.Sequence;
                    _log.AppendLine($"[{AppTimeService.LocalNow:HH:mm:ss}] {progress.LastLine}");
                    LogText.Text = _log.ToString();
                    LogScroll.ScrollToEnd();
                }
            });
        }

        public void Finish(string summary)
        {
            Dispatcher.Invoke(() =>
            {
                HeadlineText.Text = summary;
                OverallBar.IsIndeterminate = false;
                _log.AppendLine($"[{AppTimeService.LocalNow:HH:mm:ss}] {summary}");
                LogText.Text = _log.ToString();
                LogScroll.ScrollToEnd();
            });
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Topmost = false;
            if (Owner != null)
            {
                Owner.Topmost = false;
            }
        }
    }
}
