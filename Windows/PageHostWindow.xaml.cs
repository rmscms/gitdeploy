using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services;
using MahApps.Metro.Controls;

namespace GitDeployPro.Windows
{
    public partial class PageHostWindow : MetroWindow
    {
        public PageHostWindow(Page page, string title)
        {
            InitializeComponent();
            Title = title;
            HostFrame.Content = page;

            var owner = WindowOwnerService.ResolveOwner();
            if (owner != null && !ReferenceEquals(owner, this))
            {
                Owner = owner;
            }
        }
    }
}

