using System.Windows;
using GitDeployPro.Controls;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public sealed class TransferMonitorController
    {
        private TransferMonitorPanel? _panel;

        public void Attach(TransferMonitorPanel panel)
        {
            _panel = panel;
            _panel.Visibility = Visibility.Collapsed;
            _panel.HideRequested += (_, _) => Hide();
        }

        public void Show(DependencyObject? context, string title)
        {
            if (_panel == null)
            {
                return;
            }

            _panel.SetSession(title);
            _panel.Visibility = Visibility.Visible;
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
            _panel?.Finish(summary);
        }

        public void Hide()
        {
            if (_panel != null)
            {
                _panel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
