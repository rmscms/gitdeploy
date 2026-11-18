using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitDeployPro.Services.Modem
{
    /// <summary>
    /// Recognises the operator's "data usage" SMS (Hamrah-e-Aval wording is the same on every line).
    /// Tune <see cref="UsageKeywords"/> / <see cref="PercentRegex"/> here if the wording changes.
    /// </summary>
    public enum SmsKind
    {
        Other,
        Usage,
        Package,
        Promo
    }

    public sealed record PackageInfo(double Gb, int Days, string Expiry);

    public static class OperatorSmsParser
    {
        private static readonly Regex VolumeRegex = new(
            @"(\d+(?:\.\d+)?)\s*(ترابایت|گیگابایت|گیگ|مگابایت|TB|GB|MB)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DaysRegex = new(@"(\d+)\s*روزه", RegexOptions.Compiled);
        private static readonly Regex ExpiryRegex = new(@"(\d{4}/\d{2}/\d{2})", RegexOptions.Compiled);
        private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Package activation SMS ("... بسته اینترنت ... 450گیگابایت ... فعال/معتبر ...").</summary>
        public static bool TryParsePackage(string? text, out PackageInfo info)
        {
            info = new PackageInfo(0, 0, "");
            var t = Normalize(text ?? string.Empty);
            if (t.Length == 0 || !t.Contains("بسته") || !t.Contains("اینترنت") || !(t.Contains("فعال") || t.Contains("معتبر")))
            {
                return false;
            }

            // "85 درصد حجم بسته ... 450گیگابایت را مصرف کرده اید" is a usage report, not a purchase.
            if (t.Contains("مصرف کرده") || PercentRegex.IsMatch(t))
            {
                return false;
            }

            var m = VolumeRegex.Match(t);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0)
            {
                return false;
            }

            var unit = m.Groups[2].Value.ToUpperInvariant();
            var gb = unit switch
            {
                "ترابایت" or "TB" => v * 1024,
                "مگابایت" or "MB" => v / 1024,
                _ => v
            };

            var days = DaysRegex.Match(t) is { Success: true } dm ? int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var expiry = ExpiryRegex.Match(t) is { Success: true } em ? em.Groups[1].Value : "";
            info = new PackageInfo(Math.Round(gb, 2), days, expiry);
            return true;
        }

        private static readonly Regex RemainingRegex = new(
            @"(\d+(?:\.\d+)?)\s*(گیگابایت|گیگ|مگابایت|ترابایت|GB|MB|TB).{0,40}باقی",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"تنها 69121 مگابایت از حجم بسته باقی مانده است" → remaining GB.</summary>
        public static bool TryParseRemainingGb(string? text, out double gb)
        {
            gb = 0;
            var t = Normalize(text ?? string.Empty);
            var m = RemainingRegex.Match(t);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return false;
            }

            gb = m.Groups[2].Value.ToUpperInvariant() switch
            {
                "ترابایت" or "TB" => v * 1024,
                "مگابایت" or "MB" => v / 1024,
                _ => v
            };
            gb = Math.Round(gb, 2);
            return true;
        }

        public static SmsKind Classify(string? text)
        {
            if (TryParsePackage(text, out _))
            {
                return SmsKind.Package;
            }

            if (TryParseUsagePercent(text, out _))
            {
                return SmsKind.Usage;
            }

            var t = Normalize(text ?? string.Empty);
            return UrlRegex.IsMatch(t) || t.Contains("تخفیف") || t.Contains("جشنواره") || t.Contains("شارژ")
                ? SmsKind.Promo
                : SmsKind.Other;
        }

        public static IReadOnlyList<string> ExtractLinks(string? text) =>
            UrlRegex.Matches(text ?? string.Empty).Select(m => m.Value.TrimEnd('.', ',', ')', '،')).Distinct().ToList();

        private static readonly Regex UsageKeywords = new(
            "حجم|بسته|اینترنت|ترافیک|data|internet",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PercentRegex = new(
            @"(\d{1,3}(?:\.\d+)?)\s*(?:%|٪|درصد)",
            RegexOptions.Compiled);

        private static readonly Regex FinishedRegex = new(
            "(تمام|به\\s*پایان|اتمام).{0,25}(حجم|بسته|اینترنت)|(حجم|بسته|اینترنت).{0,25}(تمام|به\\s*پایان|اتمام)",
            RegexOptions.Compiled);

        /// <summary>Converts Persian/Arabic digits and separators to ASCII.</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (ch >= '۰' && ch <= '۹')
                {
                    sb.Append((char)('0' + (ch - '۰')));
                }
                else if (ch >= '٠' && ch <= '٩')
                {
                    sb.Append((char)('0' + (ch - '٠')));
                }
                else if (ch == 'ي' || ch == 'ى')
                {
                    sb.Append('ی');
                }
                else if (ch == 'ك')
                {
                    sb.Append('ک');
                }
                else if (ch == '٫' || ch == '،')
                {
                    sb.Append('.');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        /// <summary>True when the SMS reports a usage percentage (or that the plan finished).</summary>
        public static bool TryParseUsagePercent(string? text, out double percent)
        {
            percent = 0;
            var t = Normalize(text ?? string.Empty);
            if (t.Length == 0 || !UsageKeywords.IsMatch(t))
            {
                return false;
            }

            var m = PercentRegex.Match(t);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 100)
            {
                percent = p;
                return true;
            }

            if (FinishedRegex.IsMatch(t))
            {
                percent = 100;
                return true;
            }

            return false;
        }
    }
}
