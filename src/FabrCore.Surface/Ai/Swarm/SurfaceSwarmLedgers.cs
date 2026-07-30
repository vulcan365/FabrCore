namespace FabrCore.Surface.Ai.Swarm;

public sealed class ExecutionPolicy
{
    public bool NeedsPlan { get; set; }

    public string RiskLevel { get; set; } = "low";

    public int MaxConcurrency { get; set; } = 1;

    public bool ApprovalRequired { get; set; }

    public string VerificationDepth { get; set; } = "basic";

    public int ReplanThreshold { get; set; } = 1;
}

public enum SwarmStepStatus
{
    Pending = 0,
    Dispatched = 1,
    InProgress = 2,
    PendingVerification = 3,
    Completed = 4,
    Failed = 5,
    Skipped = 6
}

public sealed class TaskLedger
{
    public string Goal { get; set; } = string.Empty;

    public List<string> Facts { get; set; } = [];

    public List<string> Hypotheses { get; set; } = [];

    public List<TaskLedgerEntry> Tasks { get; set; } = [];

    public int Revision { get; set; }
}

public sealed class TaskLedgerEntry
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> DependsOn { get; set; } = [];

    public List<string> AcceptanceCriteria { get; set; } = [];

    public string AssignedAgentName { get; set; } = string.Empty;

    public string AssignedAgentHandle { get; set; } = string.Empty;

    public string? Rationale { get; set; }
}

public sealed class ProgressLedger
{
    public List<ProgressEntry> Entries { get; set; } = [];

    public ProgressEntry? FindEntry(string taskId)
        => Entries.FirstOrDefault(entry =>
            string.Equals(entry.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProgressEntry
{
    public string TaskId { get; set; } = string.Empty;

    public SwarmStepStatus Status { get; set; } = SwarmStepStatus.Pending;

    public int Attempts { get; set; }

    public int VerificationAttempts { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public string? LastFailure { get; set; }

    public string? VerifierFeedback { get; set; }
}

public sealed class ArtifactLedger
{
    public List<ArtifactEntry> Entries { get; set; } = [];
}

public sealed class ArtifactEntry
{
    public const int OutputCap = 8000;

    public string TaskId { get; set; } = string.Empty;

    public int Attempt { get; set; }

    public string Output { get; set; } = string.Empty;

    public SwarmVerdict? Verdict { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PolicyLedger
{
    public ExecutionPolicy Policy { get; set; } = new();

    public SurfaceSwarmBudgets Budgets { get; set; } = new();

    public int Round { get; set; }

    public int Replans { get; set; }

    public int ConsecutiveStalls { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public string RunId { get; set; } = string.Empty;

    public string CallerHandle { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsBlocked { get; set; }
}

public sealed class SwarmVerdict
{
    public bool Pass { get; set; }

    public List<string> Reasons { get; set; } = [];

    public List<string> MissingItems { get; set; } = [];

    public string? RetryGuidance { get; set; }
}
