using System.Windows;

namespace GitDeployPro.Windows
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; private set; } = "";

        public InputDialog(string title, string message, string defaultValue = "")
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            InputTextBox.Text = defaultValue;
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = InputTextBox.Text;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
