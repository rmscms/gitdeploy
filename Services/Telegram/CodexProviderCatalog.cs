using System;
using System.Collections.Generic;
using System.Linq;

namespace GitDeployPro.Services.Telegram
{
    public sealed class CodexProviderInfo
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string EnvKey { get; init; } = "";
        public string DefaultModel { get; init; } = "";
        public bool RequiresApiKey { get; init; } = true;
        public bool IsLocalFree { get; init; }
        public string Hint { get; init; } = "";
    }

    /// <summary>
    /// Codex CLI needs wire_api=responses. Providers here are ones that can actually speak it
    /// (OpenRouter cloud, local Ollama/LM Studio, paid OpenAI).
    /// Groq/Gemini chat-compat are NOT listed — Codex will fail without a Responses gateway.
    /// </summary>
    public static class CodexProviderCatalog
    {
        public const string OpenRouter = "openrouter";
        public const string Ollama = "ollama";
        public const string LmStudio = "lmstudio";
        public const string OpenAi = "openai";

        private static readonly CodexProviderInfo[] BuiltIn =
        {
            new()
            {
                Id = OpenRouter,
                Label = "OpenRouter (cloud free/paid)",
                BaseUrl = "https://openrouter.ai/api/v1",
                EnvKey = "OPENROUTER_API_KEY",
                DefaultModel = "poolside/laguna-s-2.1:free",
                RequiresApiKey = true,
                IsLocalFree = false,
                Hint = "Free :free models work but rate limits are tight for agents (~20/min, ~50/day)."
            },
            new()
            {
                Id = Ollama,
                Label = "Ollama (local free)",
                BaseUrl = "http://127.0.0.1:11434/v1",
                EnvKey = "OLLAMA_API_KEY",
                DefaultModel = "gpt-oss:20b",
                RequiresApiKey = false,
                IsLocalFree = true,
                Hint = "Truly free / unlimited locally. Needs Ollama ≥0.13.4 with /v1/responses. Pull a coding model (gpt-oss, qwen2.5-coder)."
            },
            new()
            {
                Id = LmStudio,
                Label = "LM Studio (local free)",
                BaseUrl = "http://127.0.0.1:1234/v1",
                EnvKey = "LM_API_KEY",
                DefaultModel = "local-model",
                RequiresApiKey = false,
                IsLocalFree = true,
                Hint = "Local free. Start LM Studio server (OpenAI-compatible) and use the loaded model id."
            },
            new()
            {
                Id = OpenAi,
                Label = "OpenAI (paid)",
                BaseUrl = "https://api.openai.com/v1",
                EnvKey = "OPENAI_API_KEY",
                DefaultModel = "gpt-5.2",
                RequiresApiKey = true,
                IsLocalFree = false,
                Hint = "Best reliability for Codex. Not free — uses your OpenAI API balance."
            },
        };

        public static IReadOnlyList<CodexProviderInfo> GetAll() => BuiltIn;

        public static CodexProviderInfo Get(string? id)
        {
            var key = Normalize(id);
            return BuiltIn.FirstOrDefault(p => p.Id == key) ?? BuiltIn[0];
        }

        public static string Normalize(string? id)
        {
            var key = (id ?? string.Empty).Trim().ToLowerInvariant();
            return BuiltIn.Any(p => p.Id == key) ? key : OpenRouter;
        }
    }

    public sealed class CodexModelInfo
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public bool IsFree { get; init; }
        public string ProviderId { get; init; } = CodexProviderCatalog.OpenRouter;
    }

    /// <summary>
    /// Suggested models per Codex provider (user can still type any slug).
    /// </summary>
    public static class CodexModelCatalog
    {
        public const string DefaultFreeCodingModel = "poolside/laguna-s-2.1:free";

        private static readonly CodexModelInfo[] BuiltIn =
        {
            // OpenRouter free coding
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "poolside/laguna-s-2.1:free", Label = "Laguna S 2.1 :free (coding agent)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "poolside/laguna-xs-2.1:free", Label = "Laguna XS 2.1 :free (small)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "cohere/north-mini-code:free", Label = "North Mini Code :free", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "nvidia/nemotron-3-super-120b-a12b:free", Label = "Nemotron 3 Super :free", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "openrouter/free", Label = "Free router (auto — 429 risk)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.OpenRouter, Id = "qwen/qwen3-coder-plus", Label = "Qwen3 Coder Plus (paid cheap)", IsFree = false },

            // Ollama local
            new() { ProviderId = CodexProviderCatalog.Ollama, Id = "gpt-oss:20b", Label = "gpt-oss:20b (local)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.Ollama, Id = "gpt-oss:120b", Label = "gpt-oss:120b (local)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.Ollama, Id = "qwen2.5-coder:14b", Label = "qwen2.5-coder:14b (local)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.Ollama, Id = "qwen2.5-coder:32b", Label = "qwen2.5-coder:32b (local)", IsFree = true },
            new() { ProviderId = CodexProviderCatalog.Ollama, Id = "deepseek-coder-v2:16b", Label = "deepseek-coder-v2:16b (local)", IsFree = true },

            // LM Studio
            new() { ProviderId = CodexProviderCatalog.LmStudio, Id = "local-model", Label = "Loaded LM Studio model id", IsFree = true },

            // OpenAI paid
            new() { ProviderId = CodexProviderCatalog.OpenAi, Id = "gpt-5.2", Label = "GPT-5.2", IsFree = false },
            new() { ProviderId = CodexProviderCatalog.OpenAi, Id = "gpt-5-mini", Label = "GPT-5 mini", IsFree = false },
            new() { ProviderId = CodexProviderCatalog.OpenAi, Id = "o4-mini", Label = "o4-mini", IsFree = false },
        };

        public static IReadOnlyList<CodexModelInfo> GetModels(string? providerId = null)
        {
            var provider = CodexProviderCatalog.Normalize(providerId);
            return BuiltIn
                .Where(m => m.ProviderId == provider)
                .OrderByDescending(m => m.IsFree)
                .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static CodexModelInfo? Find(string? id, string? providerId = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var models = string.IsNullOrWhiteSpace(providerId) ? BuiltIn : GetModels(providerId);
            return models.FirstOrDefault(m =>
                string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static string DefaultModelFor(string? providerId)
            => CodexProviderCatalog.Get(providerId).DefaultModel;
    }
}
