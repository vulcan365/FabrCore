namespace FabrCore.Surface.Ai.Swarm;

/// <summary>
/// Deterministic validation and conversion of planner ledger drafts. Executor-only
/// assignment, known dependencies, and an acyclic graph are enforced here so the
/// planner LLM cannot smuggle invalid plans into the supervisor.
/// </summary>
public static class SurfaceSwarmPlanValidation
{
    public static List<string> ValidateDraft(
        SwarmLedgerDraft draft,
        IReadOnlyList<SurfaceSwarmSquadAgent> members)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(members);

        var errors = new List<string>();
        if (draft.Tasks.Count == 0)
        {
            errors.Add("The plan contains no tasks.");
            return errors;
        }

        var executors = members
            .Where(member => member.Role == SurfaceSwarmSquadMemberRole.Executor)
            .ToList();

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in draft.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || !ids.Add(task.Id))
            {
                errors.Add($"Task '{task.Title}' has a missing or duplicate id.");
            }
        }

        foreach (var task in draft.Tasks)
        {
            if (FindExecutor(task.AssignedAgentName, executors) is null)
            {
                errors.Add(
                    $"Task '{task.Id}' is assigned to '{task.AssignedAgentName}', which is not an Executor-role member. " +
                    $"Valid executors: {string.Join(", ", executors.Select(executor => executor.Name))}.");
            }

            foreach (var dep in task.DependsOn)
            {
                if (!draft.Tasks.Any(candidate => string.Equals(candidate.Id, dep, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Task '{task.Id}' depends on unknown task '{dep}'.");
                }
            }
        }

        if (errors.Count == 0)
        {
            var probe = new TaskLedger
            {
                Tasks = draft.Tasks.Select(task => new TaskLedgerEntry
                {
                    Id = task.Id,
                    DependsOn = [.. task.DependsOn]
                }).ToList()
            };
            var (isValid, cycle) = SurfaceSwarmDependencyResolver.ValidateAcyclic(probe);
            if (!isValid)
            {
                errors.Add(cycle ?? "The dependency graph contains a cycle.");
            }
        }

        return errors;
    }

    public static TaskLedger ToLedger(
        SwarmLedgerDraft draft,
        IReadOnlyList<SurfaceSwarmSquadAgent> members,
        string goal,
        TaskLedger? priorLedger)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(members);

        var executors = members
            .Where(member => member.Role == SurfaceSwarmSquadMemberRole.Executor)
            .ToList();

        return new TaskLedger
        {
            Goal = string.IsNullOrWhiteSpace(priorLedger?.Goal) ? goal : priorLedger!.Goal,
            Facts = [.. draft.Facts],
            Hypotheses = [.. draft.Hypotheses],
            Revision = priorLedger is null ? 0 : priorLedger.Revision + 1,
            Tasks = draft.Tasks.Select(task =>
            {
                var assigned = FindExecutor(task.AssignedAgentName, executors) ?? executors[0];
                return new TaskLedgerEntry
                {
                    Id = task.Id,
                    Title = task.Title,
                    Description = task.Description,
                    DependsOn = [.. task.DependsOn],
                    AcceptanceCriteria = [.. task.AcceptanceCriteria],
                    AssignedAgentName = assigned.Name,
                    AssignedAgentHandle = assigned.Handle,
                    Rationale = task.Rationale
                };
            }).ToList()
        };
    }

    private static SurfaceSwarmSquadAgent? FindExecutor(
        string? requested,
        IReadOnlyList<SurfaceSwarmSquadAgent> executors)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return executors.FirstOrDefault(executor =>
            string.Equals(executor.Name, requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(executor.Handle, requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ShortHandle(executor.Handle), requested, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
