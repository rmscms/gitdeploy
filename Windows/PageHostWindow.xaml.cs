using System.Windows.Controls;
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
        }
    }
}

