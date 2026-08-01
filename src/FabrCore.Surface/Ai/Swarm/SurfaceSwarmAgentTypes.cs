namespace FabrCore.Surface.Ai.Swarm;

public static class SurfaceSwarmAgentTypes
{
    public const string Orchestrator = "surface-swarm-orchestrator";

    public const string Planner = "surface-swarm-planner";

    public const string Supervisor = "surface-swarm-supervisor";

    public const string Verifier = "surface-swarm-verifier";

    public const string TaskRunner = "surface-task-runner";
}

public static class SurfaceSwarmMessageTypes
{
    public const string Chat = "swarm.chat";

    public const string PlanningRequest = "swarm.planning.request";

    public const string PlanningResponse = "swarm.planning.response";

    public const string SmeConsultation = "swarm.sme.consultation";

    public const string ApprovalRequest = "swarm.approval.request";

    public const string ExecuteRequest = "swarm.execute.request";

    public const string ExecuteAccepted = "swarm.execute.accepted";

    public const string Tick = "swarm.tick";

    public const string TaskDispatch = "swarm.task.dispatch";

    public const string TaskResult = "swarm.task.result";

    public const string VerifyRequest = "swarm.verify.request";

    public const string VerifyVerdict = "swarm.verify.verdict";

    public const string Progress = "swarm.progress";

    public const string Final = "swarm.final";

    public const string StatusQuery = "swarm.status.query";

    public const string Escalation = "swarm.escalation";
}

public static class SurfaceSwarmDataTypes
{
    public const string PlanningContext = "swarm/planning-context";

    public const string TaskLedger = "swarm/task-ledger";

    public const string Execute = "swarm/execute";

    public const string Verify = "swarm/verify";

    public const string Verdict = "swarm/verdict";

    public const string Final = "swarm/final";
}

public static class SurfaceSwarmArgs
{
    public const string SquadDefinition = "SurfaceSwarm:SquadDefinition";

    public const string SquadName = "SurfaceSwarm:SquadName";

    public const string SquadSlug = "SurfaceSwarm:SquadSlug";

    public const string SquadHandle = "SurfaceSwarm:SquadHandle";

    public const string AgentName = "SurfaceSwarm:AgentName";

    public const string AgentRole = "SurfaceSwarm:AgentRole";

    public const string RunId = "SurfaceSwarm:RunId";

    public const string TaskId = "SurfaceSwarm:TaskId";

    public const string RoutedMention = "SurfaceSwarm:RoutedMention";

    public const string Mirror = "SurfaceSwarm:Mirror";

    public const string OriginalFromHandle = "SurfaceSwarm:OriginalFromHandle";

    public const string OriginalToHandle = "SurfaceSwarm:OriginalToHandle";

    public const string PendingApproval = "SurfaceSwarm:PendingApproval";

    public const string Progress = "SurfaceSwarm:Progress";
}

public static class SurfaceSwarmTimers
{
    public const string DriveLoop = "swarm-drive";
}
