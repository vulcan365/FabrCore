namespace FabrCore.Surface.Ai.Swarm;

public static class SurfaceSquadMessageTypes
{
    public const string Chat = "swarm.chat";

    public const string PlanningRequest = "swarm.planning.request";

    public const string PlanningResponse = "swarm.planning.response";

    public const string AgentRequest = "swarm.agent.request";

    public const string AgentResponse = "swarm.agent.response";

    public const string TaskTick = "surface.task.tick";

    public const string TaskDelegation = "task-delegation";

    public const string SmeConsultation = "swarm-sme-consultation";
}

public static class SurfaceSquadArgs
{
    public const string SquadDefinition = "SurfaceSwarm:SquadDefinition";

    public const string SquadName = "SurfaceSwarm:SquadName";

    public const string SquadSlug = "SurfaceSwarm:SquadSlug";

    public const string SquadHandle = "SurfaceSwarm:SquadHandle";

    public const string AgentName = "SurfaceSwarm:AgentName";

    public const string AgentRole = "SurfaceSwarm:AgentRole";

    public const string RoutedMention = "SurfaceSwarm:RoutedMention";

    public const string Mirror = "SurfaceSwarm:Mirror";

    public const string OriginalFromHandle = "SurfaceSwarm:OriginalFromHandle";

    public const string OriginalToHandle = "SurfaceSwarm:OriginalToHandle";
}
