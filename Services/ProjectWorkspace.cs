using System;

namespace GitDeployPro.Services
{
    /// <summary>Notifies listeners when the current local project folder changes.</summary>
    public static class ProjectWorkspace
    {
        public static event EventHandler<string>? CurrentProjectChanged;

        public static string CurrentPath { get; private set; } = "";

        public static void Notify(string path)
        {
            CurrentPath = path ?? string.Empty;
            CurrentProjectChanged?.Invoke(null, CurrentPath);
        }
    }
}
