using System.Windows;
using System.Windows.Controls;
using GitDeployPro.Services;
using GitDeployPro.Windows;

namespace GitDeployPro.Pages
{
    public partial class DirectUploadPage : Page
    {
        public DirectUploadPage()
        {
            InitializeComponent();
        }

        private void DetachDirectUploadPage_Click(object sender, RoutedEventArgs e)
        {
            var window = new PageHostWindow(new DirectUploadPage(), "Direct Upload • Detached");
            WindowOwnerService.ShowOwned(window, this);
        }
    }
}
