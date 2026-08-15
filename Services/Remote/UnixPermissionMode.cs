using System;

namespace GitDeployPro.Services.Remote
{
    public static class UnixPermissionMode
    {
        public const int OwnerRead = 256;
        public const int OwnerWrite = 128;
        public const int OwnerExecute = 64;
        public const int GroupRead = 32;
        public const int GroupWrite = 16;
        public const int GroupExecute = 8;
        public const int OthersRead = 4;
        public const int OthersWrite = 2;
        public const int OthersExecute = 1;

        public static int Normalize(int mode) => mode & 0x1FF;

        public static bool Has(int mode, int bit) => (Normalize(mode) & bit) != 0;

        public static int Set(int mode, int bit, bool enabled)
        {
            mode = Normalize(mode);
            return enabled ? mode | bit : mode & ~bit;
        }

        public static string ToOctal(int mode) => Convert.ToString(Normalize(mode), 8).PadLeft(3, '0');

        public static string ToSymbolic(int mode)
        {
            mode = Normalize(mode);
            return new string(new[]
            {
                Has(mode, OwnerRead) ? 'r' : '-',
                Has(mode, OwnerWrite) ? 'w' : '-',
                Has(mode, OwnerExecute) ? 'x' : '-',
                Has(mode, GroupRead) ? 'r' : '-',
                Has(mode, GroupWrite) ? 'w' : '-',
                Has(mode, GroupExecute) ? 'x' : '-',
                Has(mode, OthersRead) ? 'r' : '-',
                Has(mode, OthersWrite) ? 'w' : '-',
                Has(mode, OthersExecute) ? 'x' : '-'
            });
        }

        public static bool TryParseOctal(string? text, out int mode)
        {
            mode = 0;
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length is < 3 or > 4)
            {
                return false;
            }

            if (trimmed.Length == 4)
            {
                if (trimmed[0] != '0')
                {
                    return false;
                }

                trimmed = trimmed[1..];
            }

            foreach (var ch in trimmed)
            {
                if (ch is < '0' or > '7')
                {
                    return false;
                }
            }

            try
            {
                mode = Normalize(Convert.ToInt32(trimmed, 8));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string ExplainError(Exception ex, bool usesSsh)
        {
            var message = (ex.Message ?? string.Empty).Trim();
            var lower = message.ToLowerInvariant();

            if (lower.Contains("500") || lower.Contains("502") || lower.Contains("not understood")
                || lower.Contains("unknown command") || lower.Contains("not supported")
                || lower.Contains("not implemented") || lower.Contains("unrecognized"))
            {
                return usesSsh
                    ? "This SFTP server does not allow changing permissions."
                    : "This FTP server does not support SITE CHMOD, so permissions cannot be changed here.";
            }

            if (lower.Contains("550") || lower.Contains("permission denied") || lower.Contains("not owner")
                || lower.Contains("operation not permitted") || lower.Contains("eperm")
                || lower.Contains("eacces") || lower.Contains("access denied")
                || lower.Contains("forbidden"))
            {
                return "Access denied. This account is probably not the owner, or chmod is blocked on the server.";
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return "Could not change permissions.";
            }

            return $"Could not change permissions: {message}";
        }
    }
}
