namespace GitDeployPro.Models
{
    public static class ConnectionProfileFilters
    {
        public static bool IsRemoteFileProfile(ConnectionProfile? profile) =>
            profile != null
            && profile.DbType == DatabaseType.None
            && !string.IsNullOrWhiteSpace(profile.Host);

        public static bool IsSshTerminalProfile(ConnectionProfile? profile) =>
            IsRemoteFileProfile(profile) && profile!.UseSSH;

        public static bool IsPlainFtpProfile(ConnectionProfile? profile) =>
            IsRemoteFileProfile(profile) && !profile!.UseSSH;
    }
}
