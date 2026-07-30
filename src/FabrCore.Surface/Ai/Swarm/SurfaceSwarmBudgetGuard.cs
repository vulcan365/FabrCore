namespace FabrCore.Surface.Ai.Swarm;

public enum SwarmBudgetDecision
{
    Continue = 0,
    BudgetExhausted = 1
}

/// <summary>
/// Pure, testable budget and policy decisions extracted from the supervisor
/// drive loop and the orchestrator triage path.
/// </summary>
public static class SurfaceSwarmBudgetGuard
{
    public static SwarmBudgetDecision Evaluate(
        PolicyLedger policy,
        ProgressLedger progress,
        DateTimeOffset now)
    {
        var budgets = policy.Budgets;

        if (policy.Round > Math.Max(1, budgets.MaxRounds))
        {
            return SwarmBudgetDecision.BudgetExhausted;
        }

        if (budgets.MaxWallClockMinutes > 0
            && now - policy.StartedAt > TimeSpan.FromMinutes(budgets.MaxWallClockMinutes))
        {
            return SwarmBudgetDecision.BudgetExhausted;
        }

        var anyFailed = progress.Entries.Any(entry => entry.Status == SwarmStepStatus.Failed);
        if (anyFailed && policy.Replans >= budgets.MaxReplans && NothingReadyOrRunning(progress))
        {
            return SwarmBudgetDecision.BudgetExhausted;
        }

        return SwarmBudgetDecision.Continue;
    }

    public static bool ShouldEscalate(PolicyLedger policy)
        => policy.ConsecutiveStalls >= Math.Max(1, policy.Budgets.MaxConsecutiveStalls);

    public static bool CanReplan(PolicyLedger policy)
        => policy.Replans < policy.Budgets.MaxReplans;

    /// <summary>
    /// Clamps a classifier triage result against the squad budgets. High risk
    /// forces human approval and strict verification.
    /// </summary>
    public static ExecutionPolicy ClampTriage(SwarmTriageResult triage, SurfaceSwarmBudgets budgets)
    {
        var riskLevel = triage.RiskLevel?.Trim().ToLowerInvariant() switch
        {
            "high" => "high",
            "medium" => "medium",
            _ => "low"
        };

        var verificationDepth = triage.VerificationDepth?.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "strict" => "strict",
            _ => "basic"
        };

        var approvalRequired = triage.ApprovalRequired;
        if (riskLevel == "high")
        {
            approvalRequired = true;
            verificationDepth = "strict";
        }

        return new ExecutionPolicy
        {
            NeedsPlan = string.Equals(triage.Mode, "plan", StringComparison.OrdinalIgnoreCase),
            RiskLevel = riskLevel,
            MaxConcurrency = Math.Clamp(triage.MaxConcurrency, 1, Math.Max(1, budgets.MaxConcurrencyCeiling)),
            ApprovalRequired = approvalRequired,
            VerificationDepth = verificationDepth,
            ReplanThreshold = Math.Max(1, triage.ReplanThreshold)
        };
    }

    private static bool NothingReadyOrRunning(ProgressLedger progress)
        => !progress.Entries.Any(entry => entry.Status is SwarmStepStatus.Pending
            or SwarmStepStatus.Dispatched
            or SwarmStepStatus.InProgress
            or SwarmStepStatus.PendingVerification);
}
