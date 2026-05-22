using Orleans;

namespace FabrCore.Core;

/// <summary>
/// Lightweight class for tracking agents created by a client.
/// </summary>
public class TrackedAgentInfo
{
    public string Handle { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;

    /// <summary>
    /// Health status populated when tracked agents are requested with activation enabled.
    /// </summary>
    public AgentHealthStatus? Health { get; set; }

    public TrackedAgentInfo()
    {
    }

    public TrackedAgentInfo(string handle, string agentType)
    {
        Handle = handle;
        AgentType = agentType;
    }
}

[GenerateSerializer]
internal struct TrackedAgentInfoSurrogate
{
    [Id(0)]
    public string Handle { get; set; }

    [Id(1)]
    public string AgentType { get; set; }

    [Id(2)]
    public AgentHealthStatus? Health { get; set; }
}

[RegisterConverter]
internal sealed class TrackedAgentInfoSurrogateConverter : IConverter<TrackedAgentInfo, TrackedAgentInfoSurrogate>
{
    public TrackedAgentInfo ConvertFromSurrogate(in TrackedAgentInfoSurrogate surrogate)
    {
        return new TrackedAgentInfo
        {
            Handle = surrogate.Handle,
            AgentType = surrogate.AgentType,
            Health = surrogate.Health
        };
    }

    public TrackedAgentInfoSurrogate ConvertToSurrogate(in TrackedAgentInfo value)
    {
        return new TrackedAgentInfoSurrogate
        {
            Handle = value.Handle,
            AgentType = value.AgentType,
            Health = value.Health
        };
    }
}
