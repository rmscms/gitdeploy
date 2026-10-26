using System;
using System.Collections.Generic;
using GitDeployPro.Services;

namespace GitDeployPro.Services.Telegram
{
    public enum AgentEngineKind
    {
        Cursor,
        Claude
    }

    /// <summary>
    /// Routes Telegram agent turns to the active <see cref="IAgentDriver"/> (GlobalConfig.AgentEngine).
    /// To add another agent: implement IAgentDriver and register it in <see cref="Drivers"/>.
    /// </summary>
    public static class AgentFacade
    {
        private static readonly IReadOnlyDictionary<AgentEngineKind, IAgentDriver> Drivers =
            new Dictionary<AgentEngineKind, IAgentDriver>
            {
                [AgentEngineKind.Cursor] = CursorAgentBridge.Instance,
                [AgentEngineKind.Claude] = ClaudeAgentBridge.Instance
            };

        private static IAgentDriver Active => Drivers[GetActiveEngine()];

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
            if (string.Equals(raw?.Trim(), "claude", StringComparison.OrdinalIgnoreCase))
            {
                return AgentEngineKind.Claude;
            }

            return AgentEngineKind.Cursor;
        }

        public static string ToConfigValue(AgentEngineKind engine)
            => engine == AgentEngineKind.Claude ? "claude" : "cursor";

        public static string DisplayName(AgentEngineKind engine)
            => Drivers[engine].DisplayName;

        public static void SetActiveEngine(AgentEngineKind engine)
        {
            new ConfigurationService().UpdateGlobalConfig(cfg =>
            {
                cfg.AgentEngine = ToConfigValue(engine);
            });
        }

        public static void EnqueueUserTurn(string projectPath, string text, string? photoPath)
            => Active.EnqueueUserTurn(projectPath, text, photoPath);

        public static void PrewarmForProject(string projectPath)
            => Active.PrewarmForProject(projectPath);

        public static bool CancelCurrentTurn(string projectPath)
        {
            var any = false;
            foreach (var driver in Drivers.Values)
            {
                any |= driver.CancelCurrentTurn(projectPath);
            }

            return any;
        }

        public static bool StopTurn(string projectPath)
            => Active.StopTurn(projectPath);

        public static void RestartProjectAgent(string projectPath, string? reason = null)
            => Active.RestartProjectAgent(projectPath, reason);

        public static void SetModelAndRestart(string projectPath, string? modelId)
            => Active.SetModelAndRestart(projectPath, modelId);

        public static CursorAgentStatusInfo GetAgentStatus(string projectPath)
        {
            var driver = Active;
            var status = driver.GetAgentStatus(projectPath);

            return new CursorAgentStatusInfo
            {
                Enabled = status.Enabled,
                DaemonAlive = status.DaemonAlive,
                Model = status.Model,
                QueueDepth = status.QueueDepth,
                TurnBusy = status.TurnBusy,
                LastActivityUtc = status.LastActivityUtc,
                SessionHint = driver.DisplayName + " · " + status.SessionHint,
                AgentMode = status.AgentMode,
                LastUsage = status.LastUsage,
                SessionUsage = status.SessionUsage
            };
        }

        /// <summary>
        /// Switch engine: stop every driver's queue for the project, save preference, prewarm the new one.
        /// </summary>
        public static void SwitchEngine(string projectPath, AgentEngineKind engine)
        {
            foreach (var driver in Drivers.Values)
            {
                driver.StopTurn(projectPath);
            }

            SetActiveEngine(engine);
            PrewarmForProject(projectPath);
        }

        public static void InvalidateAfterSettingsSave(string? projectPath = null)
        {
            foreach (var driver in Drivers.Values)
            {
                driver.InvalidateAfterSettingsSave(projectPath);
            }
        }

        public static void Shutdown()
        {
            foreach (var driver in Drivers.Values)
            {
                driver.Shutdown();
            }
        }
    }
}
