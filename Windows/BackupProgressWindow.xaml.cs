using System;
using System.Text;
using System.Windows;
using GitDeployPro.Models;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class BackupProgressWindow : MetroWindow
    {
        private readonly StringBuilder _logBuilder = new();

        public BackupProgressWindow()
        {
            InitializeComponent();
        }

        public void SetModeDescription(string description)
        {
            Dispatcher.Invoke(() => ModeLabel.Text = description);
        }

        public void UpdateProgress(BackupProgressUpdate update)
        {
            if (update == null) return;

            Dispatcher.Invoke(() =>
            {
                var total = Math.Max(1, update.TotalTables);
                OverallProgressBar.Maximum = total;
                OverallProgressBar.Value = update.ProcessedTables;
                OverallSummaryText.Text = $"{update.ProcessedTables}/{total} tables";

                if (!string.IsNullOrWhiteSpace(update.CurrentTable))
                {
                    CurrentTableName.Text = $"{update.CurrentTable} ({update.CurrentTableIndex}/{total})";
                }

                var tableTotal = Math.Max(1, update.CurrentTableTotalRows);
                TableProgressBar.Maximum = tableTotal;
                TableProgressBar.Value = Math.Min(tableTotal, update.CurrentTableProcessedRows);

                TableSummaryText.Text = tableTotal > 1
                    ? $"{update.CurrentTableProcessedRows:N0} / {tableTotal:N0} rows"
                    : "no rows";

                if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    _logBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {update.Message}");
                    LogTextBlock.Text = _logBuilder.ToString();
                }
            });
        }
    }
}

