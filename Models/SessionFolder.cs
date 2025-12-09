using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace GitDeployPro.Models
{
    public class SessionFolder : INotifyPropertyChanged
    {
        private string _name = "New Folder";

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? ParentFolderId { get; set; } // null = root level
        
        public string Name 
        { 
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public List<string> ConnectionProfileIds { get; set; } = new List<string>();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

