using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SwarmTriageResult
{
    public string Mode { get; set; } = "direct";

    public string RiskLevel { get; set; } = "low";

    public bool ApprovalRequired { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    public string VerificationDepth { get; set; } = "basic";

    public int ReplanThreshold { get; set; } = 1;

    public string WorkBrief { get; set; } = string.Empty;

    public string? DirectAnswerHint { get; set; }

    public static SwarmTriageResult? Parse(string text)
        => SwarmJson.Deserialize<SwarmTriageResult>(text);
}

public sealed class SwarmLedgerDraft
{
    public List<string> Facts { get; set; } = [];

    public List<string> Hypotheses { get; set; } = [];

    public List<SwarmTaskDraft> Tasks { get; set; } = [];

    public List<string> OpenQuestions { get; set; } = [];

    public static SwarmLedgerDraft? Parse(string text)
        => SwarmJson.Deserialize<SwarmLedgerDraft>(text);
}

public sealed class SwarmTaskDraft
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> DependsOn { get; set; } = [];

    public List<string> AcceptanceCriteria { get; set; } = [];

    public string AssignedAgentName { get; set; } = string.Empty;

    public string? Rationale { get; set; }
}

public sealed class SwarmVerifierVerdict
{
    public bool Pass { get; set; }

    public List<string> Reasons { get; set; } = [];

    public List<string> MissingItems { get; set; } = [];

    public string? RetryGuidance { get; set; }

    public static SwarmVerifierVerdict? Parse(string text)
        => SwarmJson.Deserialize<SwarmVerifierVerdict>(text);

    public SwarmVerdict ToVerdict()
        => new()
        {
            Pass = Pass,
            Reasons = [.. Reasons],
            MissingItems = [.. MissingItems],
            RetryGuidance = RetryGuidance
        };
}

public sealed class SwarmPlanningContext
{
    public ExecutionPolicy Policy { get; set; } = new();

    public SurfaceSwarmBudgets Budgets { get; set; } = new();

    public TaskLedger? PriorLedger { get; set; }

    public string? ProgressSummary { get; set; }

    public string? FailureSignal { get; set; }
}

public sealed class SwarmExecutePayload
{
    public string RunId { get; set; } = string.Empty;

    public TaskLedger Ledger { get; set; } = new();

    public ExecutionPolicy Policy { get; set; } = new();

    public SurfaceSwarmBudgets Budgets { get; set; } = new();

    public string CallerHandle { get; set; } = string.Empty;
}

public sealed class SwarmVerifyPayload
{
    public string TaskId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> AcceptanceCriteria { get; set; } = [];

    public string Result { get; set; } = string.Empty;

    public string VerificationDepth { get; set; } = "basic";
}

public sealed class SwarmFinalReport
{
    public string RunId { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string Goal { get; set; } = string.Empty;

    public List<SwarmFinalTaskSummary> Tasks { get; set; } = [];

    public string? EscalationNote { get; set; }
}

public sealed class SwarmFinalTaskSummary
{
    public string TaskId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public SwarmStepStatus Status { get; set; }

    public string? ResultSummary { get; set; }

    public string? FailureReason { get; set; }
}

public static class SwarmSchema
{
    public static ChatResponseFormat For<T>(string schemaName, string schemaDescription)
        => ChatResponseFormat.ForJsonSchema(
            schema: AIJsonUtilities.CreateJsonSchema(typeof(T)),
            schemaName: schemaName,
            schemaDescription: schemaDescription);
}

public static class SwarmJson
{
    public static T? Deserialize<T>(string text)
        where T : class
    {
        var json = Extract(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, SurfaceJson.Options);

    public static string? Extract(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                return trimmed[(firstLine + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }
}
