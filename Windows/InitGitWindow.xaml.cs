using System.Collections.Generic;
using System.Windows;
using GitDeployPro.Controls;

namespace GitDeployPro.Windows
{
    public partial class InitGitWindow : Window
    {
        public bool Confirmed { get; private set; } = false;
        public List<string> SelectedBranches { get; private set; } = new List<string>();
        public string RemoteUrl { get; private set; } = "";

        public InitGitWindow(string initialRemoteUrl = "")
        {
            InitializeComponent();
            RemoteUrlTextBox.Text = initialRemoteUrl;
        }

        private void Init_Click(object sender, RoutedEventArgs e)
        {
            SelectedBranches.Clear();
            if (MainBranchCheck.IsChecked == true) SelectedBranches.Add("main");
            if (MasterBranchCheck.IsChecked == true) SelectedBranches.Add("master");
            if (BetaBranchCheck.IsChecked == true) SelectedBranches.Add("beta");
            if (DevBranchCheck.IsChecked == true) SelectedBranches.Add("development");

            if (SelectedBranches.Count == 0)
            {
                ModernMessageBox.Show("Please select at least one branch.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RemoteUrl = RemoteUrlTextBox.Text.Trim();
            Confirmed = true;
            this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }
    }
}

