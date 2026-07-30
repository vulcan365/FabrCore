namespace FabrCore.Surface.Ai.Swarm;

/// <summary>
/// Pure dependency-graph logic over the task and progress ledgers: ready-entry
/// detection, topological execution waves, cycle validation, and timeout reclaim.
/// </summary>
public static class SurfaceSwarmDependencyResolver
{
    /// <summary>
    /// Returns ledger entries whose dependencies are all Completed or Skipped,
    /// that are currently Pending, and that still have attempt budget remaining.
    /// </summary>
    public static List<TaskLedgerEntry> GetReadyEntries(
        TaskLedger ledger,
        ProgressLedger progress,
        SurfaceSwarmBudgets budgets)
    {
        var satisfiedIds = ledger.Tasks
            .Where(task => progress.FindEntry(task.Id)?.Status is SwarmStepStatus.Completed or SwarmStepStatus.Skipped)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ledger.Tasks
            .Where(task =>
            {
                var entry = progress.FindEntry(task.Id);
                return entry is { Status: SwarmStepStatus.Pending }
                    && entry.Attempts < Math.Max(1, budgets.MaxTaskAttempts)
                    && (task.DependsOn.Count == 0 || task.DependsOn.All(satisfiedIds.Contains));
            })
            .ToList();
    }

    /// <summary>
    /// Computes execution waves — groups of tasks whose dependencies are all in
    /// earlier waves. Completed/Skipped tasks are treated as already satisfied.
    /// Tasks left over after wave computation have unresolvable dependencies.
    /// </summary>
    public static List<List<TaskLedgerEntry>> GetWaves(TaskLedger ledger, ProgressLedger progress)
    {
        var remaining = ledger.Tasks
            .Where(task => progress.FindEntry(task.Id)?.Status is not (SwarmStepStatus.Completed or SwarmStepStatus.Skipped))
            .ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var satisfied = ledger.Tasks
            .Where(task => progress.FindEntry(task.Id)?.Status is SwarmStepStatus.Completed or SwarmStepStatus.Skipped)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var waves = new List<List<TaskLedgerEntry>>();
        while (remaining.Count > 0)
        {
            var wave = remaining.Values
                .Where(task => task.DependsOn.Count == 0 || task.DependsOn.All(satisfied.Contains))
                .ToList();

            if (wave.Count == 0)
            {
                break;
            }

            foreach (var task in wave)
            {
                remaining.Remove(task.Id);
                satisfied.Add(task.Id);
            }

            waves.Add(wave);
        }

        return waves;
    }

    /// <summary>
    /// True when incomplete tasks exist that can never become ready because of a
    /// cycle or a dependency on a Failed task with no attempt budget remaining.
    /// </summary>
    public static bool HasDeadlock(TaskLedger ledger, ProgressLedger progress, SurfaceSwarmBudgets budgets)
    {
        var incomplete = ledger.Tasks
            .Where(task => progress.FindEntry(task.Id)?.Status is not (SwarmStepStatus.Completed or SwarmStepStatus.Skipped))
            .ToList();
        if (incomplete.Count == 0)
        {
            return false;
        }

        if (GetReadyEntries(ledger, progress, budgets).Count > 0)
        {
            return false;
        }

        // Nothing ready — deadlocked unless work is still in flight.
        return !incomplete.Any(task => progress.FindEntry(task.Id)?.Status
            is SwarmStepStatus.Dispatched or SwarmStepStatus.InProgress or SwarmStepStatus.PendingVerification);
    }

    public static (bool IsValid, string? CycleDescription) ValidateAcyclic(TaskLedger ledger)
    {
        var taskMap = ledger.Tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in ledger.Tasks)
        {
            if (visited.Contains(task.Id))
            {
                continue;
            }

            var cycle = DetectCycle(task.Id, taskMap, visited, inStack, []);
            if (cycle is not null)
            {
                return (false, $"Cycle detected: {string.Join(" → ", cycle)}");
            }
        }

        var allIds = ledger.Tasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in ledger.Tasks)
        {
            foreach (var dep in task.DependsOn)
            {
                if (!allIds.Contains(dep))
                {
                    return (false, $"Task '{task.Id}' depends on non-existent task '{dep}'");
                }
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Returns progress entries stuck in an active state past the per-task timeout.
    /// </summary>
    public static List<ProgressEntry> GetTimedOut(
        ProgressLedger progress,
        DateTimeOffset now,
        TimeSpan perTaskTimeout)
    {
        if (perTaskTimeout <= TimeSpan.Zero)
        {
            return [];
        }

        return progress.Entries
            .Where(entry => entry.Status is SwarmStepStatus.Dispatched
                or SwarmStepStatus.InProgress
                or SwarmStepStatus.PendingVerification)
            .Where(entry => entry.DispatchedAt is not null && now - entry.DispatchedAt.Value > perTaskTimeout)
            .ToList();
    }

    private static List<string>? DetectCycle(
        string taskId,
        Dictionary<string, TaskLedgerEntry> taskMap,
        HashSet<string> visited,
        HashSet<string> inStack,
        List<string> path)
    {
        visited.Add(taskId);
        inStack.Add(taskId);
        path.Add(taskId);

        if (taskMap.TryGetValue(taskId, out var task))
        {
            foreach (var dep in task.DependsOn)
            {
                if (inStack.Contains(dep))
                {
                    path.Add(dep);
                    return path;
                }

                if (!visited.Contains(dep))
                {
                    var cycle = DetectCycle(dep, taskMap, visited, inStack, [.. path]);
                    if (cycle is not null)
                    {
                        return cycle;
                    }
                }
            }
        }

        inStack.Remove(taskId);
        return null;
    }
}
