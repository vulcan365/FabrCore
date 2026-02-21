using FabrCore.Client;
using FabrCore.Core;

namespace FabrCore.Console.CliHost.Services;

public interface IConnectionManager
{
    bool IsConnectedToAgent { get; }
    string CurrentHandle { get; }
    string? CurrentAgentHandle { get; }

    /// <summary>
    /// Fired when a "thinking" message is received from the agent.
    /// </summary>
    event Action<string>? ThinkingReceived;

    Task InitializeAsync(CancellationToken ct = default);
    void ConnectToAgent(string handle);
    Task<AgentHealthStatus> CreateAgentAsync(string agentType, string? handle = null, CancellationToken ct = default);
    Task<AgentMessage> SendMessageAsync(string message, CancellationToken ct = default);
    Task<AgentsListResponse> GetAgentsAsync(string? status = null, CancellationToken ct = default);
    Task<List<TrackedAgentInfo>> GetTrackedAgentsAsync(CancellationToken ct = default);
    Task<AgentHealthStatus> GetHealthAsync(string? handle = null, CancellationToken ct = default);
}
