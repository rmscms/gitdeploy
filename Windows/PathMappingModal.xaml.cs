using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Services.Remote;

namespace GitDeployPro.Windows
{
    public partial class PathMappingModal
    {
        private readonly ConnectionProfile _profile;
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<PathMapping> _mappings = new();
        private readonly string _clickedRemotePath;
        private PathMapping? _editing;
        private bool _dirty;

        public bool MappingsChanged => _dirty;

        public PathMappingModal(ConnectionProfile profile, string clickedRemotePath)
        {
            InitializeComponent();
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _clickedRemotePath = string.IsNullOrWhiteSpace(clickedRemotePath)
                ? "/"
                : clickedRemotePath.Trim();

            ProfileNameText.Text = string.IsNullOrWhiteSpace(_profile.Name)
                ? "Path mapping"
                : $"Path mapping — {_profile.Name}";
            ClickedPathText.Text = $"FTP folder: {_clickedRemotePath}";

            LoadMappings();
            ResetFormForNew();
            MappingsList.ItemsSource = _mappings;
            RefreshEmptyHint();
        }

        private void LoadMappings()
        {
            _mappings.Clear();
            if (_profile.PathMappings == null)
            {
                return;
            }

            foreach (var mapping in _profile.PathMappings)
            {
                if (mapping == null)
                {
                    continue;
                }

                _mappings.Add(new PathMapping
                {
                    LocalPath = RemotePathResolver.FormatLocalMappingLabel(mapping.LocalPath),
                    RemotePath = string.IsNullOrWhiteSpace(mapping.RemotePath) ? "/" : mapping.RemotePath.Trim()
                });
            }
        }

        private void ResetFormForNew()
        {
            _editing = null;
            FormTitleText.Text = "New mapping";
            SaveMappingButton.Content = "Save mapping";
            LocalPathBox.Text = "/";
            RemotePathBox.Text = _clickedRemotePath;
        }

        private void RefreshEmptyHint()
        {
            EmptyHintText.Visibility = _mappings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Persist()
        {
            _profile.PathMappings = _mappings
                .Select(m => new PathMapping
                {
                    LocalPath = m.LocalPath ?? string.Empty,
                    RemotePath = m.RemotePath ?? string.Empty
                })
                .ToList();
            _configService.AddOrUpdateConnection(_profile);
            _dirty = true;
        }

        private void SaveMapping_Click(object sender, RoutedEventArgs e)
        {
            var local = RemotePathResolver.NormalizeLocalMappingPath(LocalPathBox.Text);
            var remote = RemotePathResolver.NormalizeStoredRemoteMapping(RemotePathBox.Text, _profile.RemotePath);

            if (_editing != null)
            {
                _editing.LocalPath = local;
                _editing.RemotePath = remote;
                MappingsList.Items.Refresh();
            }
            else
            {
                _mappings.Add(new PathMapping
                {
                    LocalPath = local,
                    RemotePath = remote
                });
            }

            Persist();
            RefreshEmptyHint();
            ResetFormForNew();
        }

        private void CancelForm_Click(object sender, RoutedEventArgs e)
        {
            ResetFormForNew();
        }

        private void EditMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: PathMapping mapping })
            {
                return;
            }

            _editing = mapping;
            FormTitleText.Text = "Edit mapping";
            SaveMappingButton.Content = "Update mapping";
            LocalPathBox.Text = RemotePathResolver.FormatLocalMappingLabel(mapping.LocalPath);
            RemotePathBox.Text = string.IsNullOrWhiteSpace(mapping.RemotePath) ? "/" : mapping.RemotePath;
            LocalPathBox.Focus();
        }

        private void DeleteMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: PathMapping mapping })
            {
                return;
            }

            _mappings.Remove(mapping);
            if (ReferenceEquals(_editing, mapping))
            {
                ResetFormForNew();
            }

            Persist();
            RefreshEmptyHint();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
