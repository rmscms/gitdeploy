using System;
using GitDeployPro.Services;

namespace GitDeployPro.Services.Telegram
{
    public enum AgentEngineKind
    {
        Cursor,
        Codex
    }

    /// <summary>
    /// Routes Telegram agent turns to Cursor or Codex based on GlobalConfig.AgentEngine.
    /// </summary>
    public static class AgentFacade
    {
        public static AgentEngineKind GetActiveEngine()
        {
            try
            {
                var raw = new ConfigurationService().LoadGlobalConfig().AgentEngine;
                return ParseEngine(raw);
            }
            catch
            {
                return AgentEngineKind.Cursor;
            }
        }

        public static AgentEngineKind ParseEngine(string? raw)
        {
            if (string.Equals(raw?.Trim(), "codex", StringComparison.OrdinalIgnoreCase))
            {
                return AgentEngineKind.Codex;
            }

            return AgentEngineKind.Cursor;
        }

        public static string ToConfigValue(AgentEngineKind engine)
            => engine == AgentEngineKind.Codex ? "codex" : "cursor";

        public static string DisplayName(AgentEngineKind engine)
            => engine == AgentEngineKind.Codex ? "Codex" : "Cursor";

        public static void SetActiveEngine(AgentEngineKind engine)
        {
            new ConfigurationService().UpdateGlobalConfig(cfg =>
            {
                cfg.AgentEngine = ToConfigValue(engine);
            });
        }

        public static void EnqueueUserTurn(string projectPath, string text, string? photoPath)
        {
            if (GetActiveEngine() == AgentEngineKind.Codex)
            {
                CodexAgentBridge.Instance.EnqueueUserTurn(projectPath, text, photoPath);
            }
            else
            {
                CursorAgentBridge.Instance.EnqueueUserTurn(projectPath, text, photoPath);
            }
        }

        public static void PrewarmForProject(string projectPath)
        {
            if (GetActiveEngine() == AgentEngineKind.Codex)
            {
                CodexAgentBridge.Instance.PrewarmForProject(projectPath);
            }
            else
            {
                CursorAgentBridge.Instance.PrewarmForProject(projectPath);
            }
        }

        public static bool CancelCurrentTurn(string projectPath)
        {
            var a = CursorAgentBridge.Instance.CancelCurrentTurn(projectPath);
            var b = CodexAgentBridge.Instance.CancelCurrentTurn(projectPath);
            return a || b;
        }

        public static bool StopTurn(string projectPath)
        {
            if (GetActiveEngine() == AgentEngineKind.Codex)
            {
                return CodexAgentBridge.Instance.StopTurn(projectPath);
            }

            return CursorAgentBridge.Instance.StopTurn(projectPath);
        }

        public static void RestartProjectAgent(string projectPath, string? reason = null)
        {
            if (GetActiveEngine() == AgentEngineKind.Codex)
            {
                CodexAgentBridge.Instance.RestartProjectAgent(projectPath, reason);
            }
            else
            {
                CursorAgentBridge.Instance.RestartProjectAgent(projectPath, reason);
            }
        }

        public static void SetModelAndRestart(string projectPath, string? modelId)
        {
            if (GetActiveEngine() == AgentEngineKind.Codex)
            {
                CodexAgentBridge.Instance.SetModelAndRestart(projectPath, modelId);
            }
            else
            {
                CursorAgentBridge.Instance.SetModelAndRestart(projectPath, modelId);
            }
        }

        public static CursorAgentStatusInfo GetAgentStatus(string projectPath)
        {
            var engine = GetActiveEngine();
            var status = engine == AgentEngineKind.Codex
                ? CodexAgentBridge.Instance.GetAgentStatus(projectPath)
                : CursorAgentBridge.Instance.GetAgentStatus(projectPath);

            return new CursorAgentStatusInfo
            {
                Enabled = status.Enabled,
                DaemonAlive = status.DaemonAlive,
                Model = status.Model,
                QueueDepth = status.QueueDepth,
                TurnBusy = status.TurnBusy,
                LastActivityUtc = status.LastActivityUtc,
                SessionHint = DisplayName(engine) + " · " + status.SessionHint,
                AgentMode = status.AgentMode,
                LastUsage = status.LastUsage,
                SessionUsage = status.SessionUsage
            };
        }

        /// <summary>
        /// Switch engine: stop both queues for the project, save preference, prewarm the new one.
        /// </summary>
        public static void SwitchEngine(string projectPath, AgentEngineKind engine)
        {
            CursorAgentBridge.Instance.StopTurn(projectPath);
            CodexAgentBridge.Instance.StopTurn(projectPath);
            SetActiveEngine(engine);
            PrewarmForProject(projectPath);
        }

        public static void InvalidateAfterSettingsSave(string? projectPath = null)
        {
            CursorAgentBridge.Instance.InvalidateAfterSettingsSave(projectPath);
            CodexAgentBridge.Instance.InvalidateAfterSettingsSave(projectPath);
        }

        public static void Shutdown()
        {
            CursorAgentBridge.Instance.Shutdown();
            CodexAgentBridge.Instance.Shutdown();
        }
    }
}
