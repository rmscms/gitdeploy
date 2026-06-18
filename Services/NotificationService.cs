using System;
using System.Windows;
using GitDeployPro.Windows;

namespace GitDeployPro.Services
{
    public class NotificationService
    {
        public void ShowToast(string title, string message)
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    return;
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var toast = new ToastWindow(title, message);
                    var owner = WindowOwnerService.ResolveOwner();
                    WindowOwnerService.ShowOwned(toast, preferredOwner: owner);
                });
            }
            catch
            {
                // Ignore toast errors on unsupported systems.
            }
        }
    }
}

