using System.Collections.Generic;

namespace GitDeployPro.Services.Modem
{
    public static class ModemDriverFactory
    {
        public const string HuaweiHiLink = "HuaweiHiLink";

        /// <summary>Register new modem drivers here.</summary>
        public static IReadOnlyList<IModemDriver> All { get; } = new IModemDriver[]
        {
            new HuaweiHiLinkModemDriver()
        };

        public static IModemDriver Create(string? driverId)
        {
            foreach (var d in All)
            {
                if (string.Equals(d.DriverId, driverId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            return All[0];
        }
    }
}
