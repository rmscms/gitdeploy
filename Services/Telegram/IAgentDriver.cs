namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// One coding-agent backend (Cursor, Claude Code, ...) driven from Telegram project chats.
    /// <see cref="AgentFacade"/> picks the active driver from GlobalConfig.AgentEngine.
    /// </summary>
    public interface IAgentDriver
    {
        AgentEngineKind Kind { get; }

        string DisplayName { get; }

        void EnqueueUserTurn(string projectPath, string text, string? photoPath);

        void PrewarmForProject(string projectPath);

        bool CancelCurrentTurn(string projectPath);

        bool StopTurn(string projectPath);

        void RestartProjectAgent(string projectPath, string? reason = null);

        void SetModelAndRestart(string projectPath, string? modelId);

        CursorAgentStatusInfo GetAgentStatus(string projectPath);

        void InvalidateAfterSettingsSave(string? projectPath = null);

        void Shutdown();
    }
}
