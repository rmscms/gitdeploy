namespace GitDeployPro.Services.Update
{
    /// <summary>
    /// Per-product update host. Change <see cref="BaseUrl"/> for each Windows app.
    /// Empty BaseUrl disables automatic and manual update checks.
    /// </summary>
    public static class UpdateOptions
    {
        /// <summary>
        /// Update feed root for this app (no trailing slash required).
        /// Resolves to {BaseUrl}/latest.json
        /// </summary>
        public const string BaseUrl = "https://app.nitron.pro/gitdeploy";

        /// <summary>
        /// Minimum hours between automatic checks (startup + background timer).
        /// Manual "Check now" ignores this gate.
        /// </summary>
        public const double CheckIntervalHours = 12;

        /// <summary>
        /// How often the UI timer wakes to see if a check is due.
        /// </summary>
        public static readonly TimeSpan TimerPollInterval = TimeSpan.FromMinutes(20);

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
