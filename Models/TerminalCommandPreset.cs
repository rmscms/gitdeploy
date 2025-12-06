using System;

namespace GitDeployPro.Models
{
    public class TerminalCommandPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
    }
}

