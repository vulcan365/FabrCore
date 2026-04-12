using FabrCore.Core;
using FabrCore.Sdk.Compaction;
using FabrCore.Sdk.History;

namespace FabrCore.Sdk;

/// <summary>
/// Interface for agent proxy implementations.
/// Same contract as FabrCore.Sdk.IFabrCoreAgentProxy for drop-in replacement.
/// </summary>
public interface IFabrCoreAgentProxy
{
    internal Task InternalInitialize();
    internal Task<AgentMessage> InternalOnMessage(AgentMessage message);
    internal Task InternalOnEvent(EventMessage message);
    internal Task<ProxyHealthStatus> InternalGetHealth(HealthDetailLevel detailLevel);
    internal Task InternalReset();
    internal Task InternalFlushStateAsync();
    internal Task InternalDisposeAsync();
    internal bool InternalHasPendingStateChanges { get; }

    /// <summary>True when the proxy is currently executing an OnMessage call.</summary>
    internal bool InternalIsProcessingMessage { get; }

    /// <summary>How long the current primary OnMessage has been running. Zero if not processing.</summary>
    internal TimeSpan InternalProcessingElapsed { get; }

    /// <summary>
    /// Lightweight handler invoked when a new message arrives while OnMessage is already running.
    /// Routes to the virtual OnMessageBusy method.
    /// </summary>
    internal Task<AgentMessage> InternalOnMessageBusy(AgentMessage message);

    Task OnInitialize();
    Task<AgentMessage> OnMessage(AgentMessage message);

    /// <summary>
    /// Called when a new message arrives while the agent is already processing a message.
    /// The default implementation returns a standard "busy" response.
    /// Override to implement custom busy-state handling (e.g., acknowledge receipt,
    /// reject duplicates, provide status, or perform state-safe read-only work).
    /// IMPORTANT: Do not mutate shared agent state in this method — the primary OnMessage
    /// may be mid-execution at any await point.
    /// </summary>
    Task<AgentMessage> OnMessageBusy(AgentMessage message);
    Task OnReset();
    Task OnEvent(EventMessage message);
    Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel);
    Task<CompactionResult?> OnCompaction(FabrCoreChatHistoryProvider chatHistoryProvider, CompactionConfig compactionConfig, int estimatedTokens = 0);

    /// <summary>
    /// The current status message for heartbeat display.
    /// </summary>
    string? StatusMessage { get; set; }
}
