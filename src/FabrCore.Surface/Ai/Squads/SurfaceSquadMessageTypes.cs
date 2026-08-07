namespace FabrCore.Surface.Ai.Squads;

public static class SurfaceSquadMessageTypes
{
    public const string Chat = "squad.chat";

    public const string AgentRequest = "squad.agent.request";

    public const string AgentResponse = "squad.agent.response";

    public const string TaskTick = "surface.task.tick";

    public const string TaskDelegation = "task-delegation";

    public const string SmeConsultation = "squad-sme-consultation";
}

public static class SurfaceSquadArgs
{
    public const string SquadDefinition = "SurfaceSquad:SquadDefinition";

    public const string SquadName = "SurfaceSquad:SquadName";

    public const string SquadSlug = "SurfaceSquad:SquadSlug";

    public const string SquadHandle = "SurfaceSquad:SquadHandle";

    public const string AgentName = "SurfaceSquad:AgentName";

    public const string AgentRole = "SurfaceSquad:AgentRole";

    public const string RoutedMention = "SurfaceSquad:RoutedMention";

    public const string Mirror = "SurfaceSquad:Mirror";

    public const string OriginalFromHandle = "SurfaceSquad:OriginalFromHandle";

    public const string OriginalToHandle = "SurfaceSquad:OriginalToHandle";
}
