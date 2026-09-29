using GitDeployPro.Models;

namespace GitDeployPro.Services.Telegram
{
    internal static class TelegramTerminalCommandCatalog
    {
        public const string GoBuildExcoreCommand =
            "cd /var/www/crypto.nitron.pro/core && CGO_ENABLED=0 go build -o bin/excore ./cmd/excore && sudo systemctl restart excore && sudo systemctl status excore --no-pager";

        public static TelegramTerminalCommand CreateGoBuildExcore(string projectPath)
        {
            return new TelegramTerminalCommand
            {
                Description = "Go build",
                Command = GoBuildExcoreCommand,
                ProjectPath = projectPath,
                NotifyBeforeRun = true,
                NotifyMessage = string.Empty
            };
        }
    }
}
