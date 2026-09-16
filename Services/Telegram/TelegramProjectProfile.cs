using GitDeployPro.Models;
using GitDeployPro.Services.Localization;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Per-project Telegram behavior (deploy button, pending hints, etc.).
    /// </summary>
    public static class TelegramProjectProfile
    {
        public static ProjectKind GetKind(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || TelegramPaths.IsUnassigned(projectPath))
            {
                return ProjectKind.WebFtpDeploy;
            }

            try
            {
                var config = new ConfigurationService().LoadProjectConfig(projectPath);
                return config.ProjectKind;
            }
            catch
            {
                return ProjectKind.WebFtpDeploy;
            }
        }

        public static bool SupportsDeploy(string? projectPath)
            => GetKind(projectPath) == ProjectKind.WebFtpDeploy;

        public static string GetKindLabel(string? projectPath)
        {
            return GetKind(projectPath) switch
            {
                ProjectKind.WindowsDesktop => Loc.T("settings.projectKind.windowsDesktop"),
                ProjectKind.NoDeploy => Loc.T("settings.projectKind.noDeploy"),
                _ => Loc.T("settings.projectKind.webFtp")
            };
        }
    }
}
