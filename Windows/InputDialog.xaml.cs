using System.Windows;
using System.Windows.Input;

namespace GitDeployPro.Windows
{
    public partial class InputDialog
    {
        public string InputText { get; private set; } = "";
        public string ResponseText => InputText; // Alias for compatibility

        public InputDialog(string prompt, string title, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptTextBlock.Text = prompt;
            InputTextBox.Text = defaultValue;
            InputTextBox.SelectAll();
            InputTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            InputText = InputTextBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
            }
        }
    }
}
