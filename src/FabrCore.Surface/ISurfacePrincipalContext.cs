using FabrCore.Core;

namespace FabrCore.Surface;

public interface ISurfacePrincipalContext : IAsyncDisposable
{
    string Handle { get; }

    bool IsDisposed { get; }

    event EventHandler<AgentMessage>? AgentMessageReceived;

    Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);

    Task SendMessage(AgentMessage request);

    Task SendEvent(EventMessage request);

    Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration);

    Task<AgentHealthStatus> ResetAgent(string handle);

    Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

    Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false);

    Task<bool> IsAgentTracked(string handle);

    Task<List<AgentInfo>> GetAccessibleSharedAgents();
}
