using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using GitDeployPro.Controls;

namespace GitDeployPro.Windows
{
    public partial class CloneRepoWindow : Window
    {
        public string RemoteUrl { get; private set; } = "";
        public string LocalPath { get; private set; } = "";

        private bool _suppressPathEvents;
        private bool _pathManuallyEdited;

        public CloneRepoWindow()
        {
            InitializeComponent();
            HostTextBox.Text = "github.com";
            RepoInputTextBox.Focus();
            ApplyDefaultPath();
            UpdateRemotePreview();
        }

        private void RepoInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateRemotePreview();
        }

        private void HostTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateRemotePreview();
        }

        private void ProtocolComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRemotePreview();
        }

        private void LocalPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPathEvents) return;
            if (LocalPathTextBox.IsKeyboardFocusWithin)
            {
                _pathManuallyEdited = true;
            }
        }

        private void UseDefaultPath_Click(object sender, RoutedEventArgs e)
        {
            ApplyDefaultPath(force: true);
        }

        private void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select parent folder for cloning",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var repoName = GetRepositoryName();
                _suppressPathEvents = true;
                LocalPathTextBox.Text = Path.Combine(dialog.SelectedPath, repoName);
                _suppressPathEvents = false;
                _pathManuallyEdited = true;
            }
        }

        private void CloneButton_Click(object sender, RoutedEventArgs e)
        {
            var remote = BuildRemoteUrl();
            if (string.IsNullOrWhiteSpace(remote))
            {
                ModernMessageBox.Show("Please enter a repository slug or remote URL.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targetPath = LocalPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                ModernMessageBox.Show("Select a local destination folder.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RemoteUrl = remote;
            LocalPath = targetPath;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private string BuildRemoteUrl()
        {
            if (RepoInputTextBox == null) return "";
            
            var input = RepoInputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return "";

            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                return EnsureGitSuffix(input);
            }

            var host = (HostTextBox == null || string.IsNullOrWhiteSpace(HostTextBox.Text)) ? "github.com" : HostTextBox.Text.Trim();
            var slug = TrimGitSuffix(input.Trim().Trim('/'));
            
            string protocolLabel = "HTTPS";
            if (ProtocolComboBox != null && ProtocolComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                protocolLabel = item.Content.ToString() ?? "HTTPS";
            }

            if (protocolLabel.Equals("SSH", StringComparison.OrdinalIgnoreCase))
            {
                return $"git@{host}:{slug}.git";
            }

            return $"https://{host}/{slug}.git";
        }

        private void UpdateRemotePreview()
        {
            if (PreviewUrlTextBox == null) return;
            
            PreviewUrlTextBox.Text = BuildRemoteUrl();

            if (!_pathManuallyEdited)
            {
                ApplyDefaultPath();
            }
        }

        private void ApplyDefaultPath(bool force = false)
        {
            if ((_pathManuallyEdited && !force) || LocalPathTextBox == null) return;

            var repoName = GetRepositoryName();
            var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitDeployProjects");

            try { Directory.CreateDirectory(baseDirectory); } catch { }

            _suppressPathEvents = true;
            LocalPathTextBox.Text = Path.Combine(baseDirectory, repoName);
            _suppressPathEvents = false;
            _pathManuallyEdited = false;
        }

        private string GetRepositoryName()
        {
            if (RepoInputTextBox == null) return "GitDeployProject";

            var input = RepoInputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return "GitDeployProject";
            }

            string slug = input;

            if (slug.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(slug);
                    slug = uri.AbsolutePath.Trim('/');
                }
                catch
                {
                    // ignore parsing errors
                }
            }
            else if (slug.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = slug.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < slug.Length - 1)
                {
                    slug = slug.Substring(colonIndex + 1);
                }
            }

            slug = slug.Trim('/').Replace("\\", "/");
            var parts = slug.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var repoName = parts.LastOrDefault() ?? "GitDeployProject";
            repoName = TrimGitSuffix(repoName);
            if (string.IsNullOrWhiteSpace(repoName))
            {
                repoName = "GitDeployProject";
            }
            return repoName;
        }

        private static string TrimGitSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var trimmed = value.Trim();
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4);
            }
            return trimmed;
        }

        private static string EnsureGitSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var trimmed = value.Trim();
            if (!trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                trimmed += ".git";
            }
            return trimmed;
        }
    }
}

