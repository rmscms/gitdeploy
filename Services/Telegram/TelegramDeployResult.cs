namespace GitDeployPro.Services.Telegram
{
    public sealed class TelegramDeployResult
    {
        public bool Ok { get; init; }
        public int ChangeCount { get; init; }
        public int UploadedCount { get; init; }
        public bool CommitDone { get; init; }
        public bool PushOk { get; init; }
        public string Summary { get; init; } = string.Empty;

        public static TelegramDeployResult Fail(string summary, int changeCount = 0)
            => new() { Ok = false, ChangeCount = changeCount, Summary = summary };

        public static TelegramDeployResult Succeed(
            int changeCount,
            int uploadedCount,
            bool pushOk,
            string summary)
            => new()
            {
                Ok = true,
                ChangeCount = changeCount,
                UploadedCount = uploadedCount,
                CommitDone = true,
                PushOk = pushOk,
                Summary = summary
            };
    }
}
