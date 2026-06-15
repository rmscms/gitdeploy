using System;
using System.Globalization;

namespace GitDeployPro.Services
{
    /// <summary>
    /// Central time service for writing timestamps and presenting them
    /// in the current Windows local timezone consistently.
    /// </summary>
    public static class AppTimeService
    {
        public const string DefaultDateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        public static TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        public static DateTime UtcNow => DateTime.UtcNow;

        public static DateTime LocalNow => ToLocalFromUtc(UtcNow);

        public static DateTimeOffset LocalNowOffset => DateTimeOffset.Now;

        public static DateTime NormalizeUtc(DateTime value, bool assumeUtcForUnspecified = true)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => assumeUtcForUnspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
            };
        }

        public static DateTime ToLocalFromUtc(DateTime utcValue)
        {
            return NormalizeUtc(utcValue, assumeUtcForUnspecified: true).ToLocalTime();
        }

        public static DateTime ToLocal(DateTime value, bool assumeUtcForUnspecified = false)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value.ToLocalTime(),
                DateTimeKind.Local => value,
                _ => assumeUtcForUnspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
                    : DateTime.SpecifyKind(value, DateTimeKind.Local)
            };
        }

        public static DateTime? ToLocalFromUtc(DateTime? utcValue)
        {
            return utcValue.HasValue ? ToLocalFromUtc(utcValue.Value) : null;
        }

        public static string FormatLocalFromUtc(DateTime utcValue, string format = DefaultDateTimeFormat)
        {
            return ToLocalFromUtc(utcValue).ToString(format, CultureInfo.CurrentCulture);
        }

        public static string FormatLocalFromUtc(DateTime? utcValue, string format = DefaultDateTimeFormat, string empty = "—")
        {
            return utcValue.HasValue ? FormatLocalFromUtc(utcValue.Value, format) : empty;
        }

        public static string FormatLocal(DateTime localValue, string format = DefaultDateTimeFormat)
        {
            var value = localValue.Kind == DateTimeKind.Utc ? localValue.ToLocalTime() : localValue;
            return value.ToString(format, CultureInfo.CurrentCulture);
        }
    }
}
