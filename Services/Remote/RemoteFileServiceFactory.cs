using GitDeployPro.Models;

namespace GitDeployPro.Services.Remote
{
    public static class RemoteFileServiceFactory
    {
        public static IRemoteFileService Create(ConnectionProfile profile)
        {
            if (profile == null)
            {
                throw new System.ArgumentNullException(nameof(profile));
            }

            return profile.UseSSH
                ? new SftpRemoteFileService()
                : new FtpRemoteFileService();
        }
    }
}
