using Orleans;

namespace FabrCore.Core
{
    [GenerateSerializer]
    public enum AgentStatus
    {
        [Id(0)]
        Active,
        [Id(1)]
        Deactivated
    }

    [GenerateSerializer]
    public enum EntityType
    {
        [Id(0)]
        Agent,
        [Id(1)]
        Client
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
