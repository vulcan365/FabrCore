using Orleans;

namespace FabrCore.Core
{
    public enum AgentStatus
    {
        Active,
        Deactivated
    }

    public enum EntityType
    {
        Agent,
        Principal
    }

    [GenerateSerializer]
    public record AgentInfo(
        [property: Id(0)] string Key,
        [property: Id(1)] string AgentType,
        [property: Id(2)] string Handle,
        [property: Id(3)] AgentStatus Status,
        [property: Id(4)] DateTime ActivatedAt,
        [property: Id(5)] DateTime? DeactivatedAt,
        [property: Id(6)] string? DeactivationReason,
        [property: Id(7)] EntityType EntityType = EntityType.Agent
    );
}
