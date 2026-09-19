using System;
using GitDeployPro.Models;

namespace GitDeployPro.Services.Telegram
{
    internal static class TelegramChatChrome
    {
        private static readonly string[] ButtonLabels =
        {
            "/start",
            "/help",
            "/status",
            "/projects",
            "/deploy",
            "/clear",
            "/wipe",
            "📂 Projects",
            "📂 پروژه‌ها",
            "● Status",
            "● وضعیت",
            "Help",
            "راهنما",
            "Projects",
            "پروژه‌ها",
            "Status",
            "وضعیت",
            "🚀 Deploy",
            "🚀 دیپلوی",
            "Deploy",
            "دیپلوی",
            "🧹 Clear chat",
            "🧹 پاک کردن چت",
            "Clear chat",
            "پاک کردن چت",
            "🗑 Clear Telegram",
            "🗑 پاک تلگرام",
            "Clear Telegram",
            "پاک تلگرام",
            "/restart",
            "/model",
            "/models",
            "♻️ Restart agent",
            "♻️ ریستارت ایجنت",
            "Restart agent",
            "ریستارت ایجنت",
            "🧠 Model",
            "🧠 مدل",
            "Model",
            "مدل"
        };

        private static readonly string[] BotPhrases =
        {
            "Active project:",
            "پروژه فعال:",
            "Active chat:",
            "چت فعال:",
            "Tap a project to switch",
            "روی پروژه بزن تا چت فعال عوض شود",
            "Choose a project",
            "یک پروژه انتخاب کن",
            "Send a photo or a short note",
            "Send a photo, a txt/md file, or a short note",
            "عکس یا یک یادداشت کوتاه بفرست",
            "عکس، فایل txt/md، یا یک یادداشت کوتاه بفرست",
            "Tap Projects and choose a chat",
            "پروژه‌ها را بزن و یکی را انتخاب کن",
            "Projects:",
            "پروژه‌ها:"
        };

        public static bool IsNoise(TelegramChatMessage? message)
        {
            if (message == null)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(message.PhotoPath)
                || !string.IsNullOrWhiteSpace(message.TelegramFileId)
                || !string.IsNullOrWhiteSpace(message.AttachmentPath))
            {
                return false;
            }

            var text = (message.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (message.Direction == TelegramMessageDirection.System)
            {
                // Keep Cursor/progress status lines; drop old Telegram menu chrome only.
                foreach (var phrase in BotPhrases)
                {
                    if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (text.StartsWith('/'))
            {
                return true;
            }

            var normalized = Normalize(text);
            foreach (var label in ButtonLabels)
            {
                if (string.Equals(normalized, Normalize(label), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var phrase in BotPhrases)
            {
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Messages useful in the UI but useless / harmful as Cursor prompt context
        /// (status lines, self-intro fluff).
        /// </summary>
        public static bool IsAgentContextNoise(TelegramChatMessage? message)
        {
            if (IsNoise(message))
            {
                return true;
            }

            if (message!.Direction == TelegramMessageDirection.System)
            {
                return true;
            }

            var text = (message.Text ?? string.Empty).Trim();
            if (text.Contains("آماده‌ام", StringComparison.OrdinalIgnoreCase)
                || text.Contains("I am ready", StringComparison.OrdinalIgnoreCase)
                || text.Contains("من agent کدنویسی", StringComparison.OrdinalIgnoreCase)
                || text.Contains("coding agent for this project", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Cursor agent started", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ایجنت Cursor شروع شد", StringComparison.OrdinalIgnoreCase)
                || text.Contains("CRITICAL INSTRUCTIONS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("بعدش متنی نیست", StringComparison.OrdinalIgnoreCase)
                || text.Contains("فقط همین است", StringComparison.OrdinalIgnoreCase)
                || text.Contains("متن دستورات نیامده", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Replace("📂", "", StringComparison.Ordinal)
                .Replace("📁", "", StringComparison.Ordinal)
                .Replace("●", "", StringComparison.Ordinal)
                .Replace("✅", "", StringComparison.Ordinal)
                .Replace("ℹ️", "", StringComparison.Ordinal)
                .Trim();
        }
    }
}
