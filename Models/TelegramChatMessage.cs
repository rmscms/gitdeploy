using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GitDeployPro.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TelegramMessageDirection
    {
        Incoming,
        Outgoing,
        System
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TelegramMessageStatus
    {
        Received,
        Sent,
        Failed
    }

    public class TelegramChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public TelegramMessageDirection Direction { get; set; } = TelegramMessageDirection.Incoming;
        public TelegramMessageStatus Status { get; set; } = TelegramMessageStatus.Received;
        public string Text { get; set; } = "";
        public string PhotoPath { get; set; } = "";
        public string TelegramFileId { get; set; } = "";
        public long TelegramMessageId { get; set; }
        public long ChatId { get; set; }
        public long UserId { get; set; }
        public string SenderName { get; set; } = "";
        public DateTime Utc { get; set; } = DateTime.UtcNow;
    }

    public class TelegramThread
    {
        public string ProjectPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime? LastReadUtc { get; set; }
        /// <summary>Legacy / CLI --resume session id.</summary>
        public string CursorSessionId { get; set; } = "";
        /// <summary>Warm ACP session id (not valid for agent -p --resume).</summary>
        public string CursorAcpSessionId { get; set; } = "";
        /// <summary>One-shot CLI --resume session id.</summary>
        public string CursorCliSessionId { get; set; } = "";
        /// <summary>Bot-sent Telegram message ids (for wipe), newest last.</summary>
        public List<long> TrackedBotMessageIds { get; set; } = new();
        public List<TelegramChatMessage> Messages { get; set; } = new();
    }

    public class TelegramRuntimeState
    {
        public long LastUpdateId { get; set; }
        public string ActiveProjectPath { get; set; } = "";
        public long LastChatId { get; set; }
    }

    public class TelegramThreadSummary
    {
        public string ProjectPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string LastPreview { get; set; } = "";
        public DateTime? LastMessageUtc { get; set; }
        public int UnreadCount { get; set; }
        public bool HasThreadFile { get; set; }
    }

    public sealed class TelegramIncomingUpdate
    {
        public long UpdateId { get; init; }
        public long MessageId { get; init; }
        public long ChatId { get; init; }
        public long UserId { get; init; }
        public string UserName { get; init; } = "";
        public string Text { get; init; } = "";
        public string Caption { get; init; } = "";
        public string PhotoFileId { get; init; } = "";
        public bool IsCallback { get; init; }
        public string CallbackQueryId { get; init; } = "";
        public string CallbackData { get; init; } = "";
    }

    public sealed class TelegramMessageEventArgs : EventArgs
    {
        public string ProjectPath { get; init; } = "";
        public TelegramChatMessage Message { get; init; } = new();
    }
}
