using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Classifies Cursor ACP/CLI capacity errors (busy model server, rate limits).
    /// Cursor has no live "least busy model" API — we suggest alternates from the CLI catalog.
    /// </summary>
    internal static class CursorAcpErrorHelper
    {
        public static bool IsCapacityError(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lower = text.ToLowerInvariant();
            return lower.Contains("resource_exhausted", StringComparison.Ordinal)
                   || lower.Contains("error_resource_exhausted", StringComparison.Ordinal)
                   || lower.Contains("retriableerror", StringComparison.Ordinal)
                   || lower.Contains("rate limit", StringComparison.Ordinal)
                   || lower.Contains("rate_limited", StringComparison.Ordinal)
                   || lower.Contains("rate limited", StringComparison.Ordinal)
                   || lower.Contains("too many requests", StringComparison.Ordinal)
                   || lower.Contains("overloaded", StringComparison.Ordinal)
                   || lower.Contains("capacity", StringComparison.Ordinal)
                   || lower.Contains("high load", StringComparison.Ordinal)
                   || lower.Contains("high demand", StringComparison.Ordinal)
                   || lower.Contains("unable to reach the model provider", StringComparison.Ordinal)
                   || lower.Contains("trouble connecting to the model provider", StringComparison.Ordinal)
                   || lower.Contains("503", StringComparison.Ordinal)
                   || lower.Contains("429", StringComparison.Ordinal);
        }

        public static bool IsCapacityError(Exception? ex)
            => ex != null && IsCapacityError(ex.Message);

        /// <summary>Assistant output that is only an error blob (not a real answer).</summary>
        public static bool IsFailureOutput(string? output)
        {
            var text = (output ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            if (!IsCapacityError(text))
            {
                return false;
            }

            // Short error-only replies.
            if (text.Length < 400)
            {
                return true;
            }

            // Error prefix with little real content after.
            if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("RetriableError", StringComparison.OrdinalIgnoreCase))
            {
                return text.Length < 1200;
            }

            return false;
        }

        public static async Task<IReadOnlyList<string>> SuggestAlternateModelsAsync(
            string? currentModel,
            CancellationToken cancellationToken)
        {
            var current = string.IsNullOrWhiteSpace(currentModel) ? "auto" : currentModel.Trim();
            var currentFamily = CursorModelCatalog.DetectFamily(current);
            var suggestions = new List<string>();

            if (!string.Equals(current, "auto", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("auto");
            }

            try
            {
                var all = await CursorModelCatalog.Instance
                    .GetModelsAsync(forceRefresh: false, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var m in all
                             .OrderBy(m => FamilyRank(m.Family))
                             .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    if (suggestions.Count >= 3)
                    {
                        break;
                    }

                    if (string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (suggestions.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Prefer a different model family first (often less contended).
                    if (string.Equals(m.Family, currentFamily, StringComparison.OrdinalIgnoreCase)
                        && suggestions.Count >= 1
                        && !string.Equals(current, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    suggestions.Add(m.Id);
                }
            }
            catch
            {
            }

            if (suggestions.Count == 0 && !string.Equals(current, "auto", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("auto");
            }

            return suggestions;
        }

        public static string ShortModelLabel(string modelId)
        {
            var id = (modelId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || id is "auto" or "default")
            {
                return "auto";
            }

            var slash = id.LastIndexOf('/');
            if (slash >= 0 && slash < id.Length - 1)
            {
                id = id[(slash + 1)..];
            }

            if (id.Length > 22)
            {
                id = id[..19] + "…";
            }

            return id;
        }

        private static int FamilyRank(string family)
            => (family ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "grok" => 0,
                "composer" => 1,
                "claude" => 2,
                "gpt" => 3,
                "codex" => 4,
                "gemini" => 5,
                "auto" => 6,
                _ => 9
            };
    }
}
