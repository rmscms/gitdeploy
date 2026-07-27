using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GitDeployPro.Services.Update;

namespace GitDeployPro.Windows
{
    public partial class WhatsNewWindow : Window
    {
        public WhatsNewWindow(string version, IEnumerable<string> changelogItems)
        {
            InitializeComponent();
            VersionText.Text = $"Version {version}";
            var items = changelogItems?
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList()
                ?? new List<string>();

            if (items.Count == 0)
            {
                items.Add("This release includes improvements and fixes.");
            }

            ChangelogList.ItemsSource = items;
        }

        private void GotItButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
