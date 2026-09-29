using System;

namespace GitDeployPro.Models
{
    /// <summary>
    /// Trusted one-line SSH preset for Telegram terminal (inline buttons). Bypasses the free-text guard.
    /// Scoped per project path.
    /// </summary>
    public class TelegramTerminalCommand
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Description { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;

        /// <summary>When true, send <see cref="NotifyMessage"/> (or auto text) on Telegram before running.</summary>
        public bool NotifyBeforeRun { get; set; }

        /// <summary>Optional HTML-safe plain text sent before the command (e.g. before systemctl restart).</summary>
        public string NotifyMessage { get; set; } = string.Empty;

        public string ButtonLabel =>
            string.IsNullOrWhiteSpace(Description) ? TrimCommandLabel(Command) : Description.Trim();

        private static string TrimCommandLabel(string command)
        {
            var text = (command ?? string.Empty).Trim();
            if (text.Length <= 32)
            {
                return text;
            }

            return text[..29] + "…";
        }
    }
}
