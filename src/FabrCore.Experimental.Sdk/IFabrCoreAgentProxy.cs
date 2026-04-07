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

    Task OnInitialize();
    Task<AgentMessage> OnMessage(AgentMessage message);
    Task OnReset();
    Task OnEvent(EventMessage message);
    Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel);
    Task<CompactionResult?> OnCompaction(FabrCoreChatHistoryProvider chatHistoryProvider, CompactionConfig compactionConfig, int estimatedTokens = 0);

    /// <summary>
    /// The current status message for heartbeat display.
    /// </summary>
    string? StatusMessage { get; set; }
}
