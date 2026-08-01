using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

[AgentAlias(SurfaceSwarmAgentTypes.Supervisor)]
[Description("Built-in execution supervisor for Surface Swarm squads.")]
[FabrCoreCapabilities("Owns Swarm run execution: persists task/progress/artifact/policy ledgers, drives dependency waves on a timer loop, dispatches work to executor members, gates results through the verifier, retries with feedback, replans on stalls, consults SMEs before escalating, and enforces hard budgets.")]
[FabrCoreNote("Send swarm.execute.request with a SwarmExecutePayload to start a run; one run executes at a time per squad.")]
public sealed class SurfaceSwarmSupervisorAgent : FabrCoreAgentProxy
{
    private const string TaskLedgerStateKey = "sup-task-ledger";
    private const string ProgressLedgerStateKey = "sup-progress-ledger";
    private const string ArtifactLedgerStateKey = "sup-artifact-ledger";
    private const string PolicyLedgerStateKey = "sup-policy-ledger";
    private const int DependencyContextCap = 2000;

    private SurfaceSwarmSquadRuntime runtime = new();
    private SurfaceSwarmSquadConversationBus? bus;
    private TaskLedger taskLedger = new();
    private ProgressLedger progress = new();
    private ArtifactLedger artifacts = new();
    private PolicyLedger policy = new();
    private volatile string statusSnapshot = "Idle.";
    private readonly ILogger<SurfaceSwarmSupervisorAgent> supervisorLogger;

    public SurfaceSwarmSupervisorAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        supervisorLogger = loggerFactory.CreateLogger<SurfaceSwarmSupervisorAgent>();
    }

    public override async Task OnInitialize()
    {
        runtime = SurfaceSwarmSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        bus = new SurfaceSwarmSquadConversationBus(fabrcoreAgentHost, runtime);

        taskLedger = await LoadStateAsync<TaskLedger>(TaskLedgerStateKey);
        progress = await LoadStateAsync<ProgressLedger>(ProgressLedgerStateKey);
        artifacts = await LoadStateAsync<ArtifactLedger>(ArtifactLedgerStateKey);
        policy = await LoadStateAsync<PolicyLedger>(PolicyLedgerStateKey);
        RefreshSnapshot();

        if (policy.IsRunning)
        {
            supervisorLogger.LogInformation(
                "Swarm supervisor restored with an active run; resuming drive loop - Handle: {Handle}, RunId: {RunId}, Round: {Round}",
                fabrcoreAgentHost.GetHandle(),
                policy.RunId,
                policy.Round);
            ScheduleTick(TimeSpan.FromMilliseconds(100));
        }
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        if (message.MessageType == SurfaceSwarmMessageTypes.Tick)
        {
            fabrcoreAgentHost.UnregisterTimer(SurfaceSwarmTimers.DriveLoop);
            await DriveAsync();
            return message.Response();
        }

        var response = message.Response();
        bus?.Stamp(response);

        if (message.MessageType == SurfaceSwarmMessageTypes.ExecuteRequest)
        {
            return await AcceptRunAsync(message, response);
        }

        response.MessageType = SurfaceSwarmMessageTypes.StatusQuery;
        response.Message = statusSnapshot;
        return response;
    }

    public override Task<AgentMessage> OnMessageBusy(AgentMessage message)
    {
        if (message.MessageType == SurfaceSwarmMessageTypes.Tick)
        {
            // The next scheduled tick will pick the work back up.
            return Task.FromResult(message.Response());
        }

        if (message.MessageType == SurfaceSwarmMessageTypes.StatusQuery)
        {
            var status = message.Response();
            status.Message = statusSnapshot;
            return Task.FromResult(status);
        }

        var response = message.Response();
        response.Message = policy.IsRunning
            ? $"The supervisor is executing run {policy.RunId}. {statusSnapshot}"
            : "The supervisor is busy. Try again shortly.";
        return Task.FromResult(response);
    }

    private async Task<AgentMessage> AcceptRunAsync(AgentMessage message, AgentMessage response)
    {
        if (bus is null)
        {
            response.Message = "Swarm supervisor is not initialized.";
            return response;
        }

        if (policy.IsRunning)
        {
            response.Message = $"Run {policy.RunId} is already executing. One run at a time per squad.";
            return response;
        }

        var payload = ReadPayload<SwarmExecutePayload>(message);
        if (payload is null || payload.Ledger.Tasks.Count == 0)
        {
            response.Message = "The execute request payload could not be parsed or contains no tasks.";
            return response;
        }

        var (isValid, cycle) = SurfaceSwarmDependencyResolver.ValidateAcyclic(payload.Ledger);
        if (!isValid)
        {
            response.Message = $"The task ledger is invalid: {cycle}";
            return response;
        }

        taskLedger = payload.Ledger;
        progress = new ProgressLedger
        {
            Entries = payload.Ledger.Tasks
                .Select(task => new ProgressEntry { TaskId = task.Id })
                .ToList()
        };
        artifacts = new ArtifactLedger();
        policy = new PolicyLedger
        {
            Policy = payload.Policy,
            Budgets = payload.Budgets,
            RunId = payload.RunId,
            CallerHandle = string.IsNullOrWhiteSpace(payload.CallerHandle)
                ? runtime.Squad.OrchestratorHandle
                : payload.CallerHandle,
            StartedAt = DateTimeOffset.UtcNow,
            IsRunning = true
        };

        await PersistAsync();
        await MirrorProgressAsync($"Run {policy.RunId} accepted with {taskLedger.Tasks.Count} tasks.");
        ScheduleTick(TimeSpan.FromMilliseconds(100));

        response.MessageType = SurfaceSwarmMessageTypes.ExecuteAccepted;
        response.Message = $"Accepted run {policy.RunId} with {taskLedger.Tasks.Count} task{(taskLedger.Tasks.Count == 1 ? string.Empty : "s")}.";
        return response;
    }

    private async Task DriveAsync()
    {
        if (!policy.IsRunning || bus is null)
        {
            return;
        }

        policy.Round++;

        if (SurfaceSwarmBudgetGuard.Evaluate(policy, progress, DateTimeOffset.UtcNow)
            == SwarmBudgetDecision.BudgetExhausted)
        {
            await FinishRunAsync("budget-exhausted", escalate: true,
                escalationNote: $"Budgets exhausted after {policy.Round} rounds and {policy.Replans} replans.");
            return;
        }

        ReclaimTimedOut();

        if (progress.Entries.All(entry => entry.Status is SwarmStepStatus.Completed or SwarmStepStatus.Skipped))
        {
            await FinishRunAsync("completed", escalate: false, escalationNote: null);
            return;
        }

        var ready = SurfaceSwarmDependencyResolver.GetReadyEntries(taskLedger, progress, policy.Budgets);
        if (ready.Count > 0)
        {
            policy.ConsecutiveStalls = 0;
            await DispatchWaveAsync(ready);
            if (policy.IsRunning)
            {
                ScheduleTick(DriveLoopInterval());
            }

            return;
        }

        await HandleStallAsync();
    }

    private async Task DispatchWaveAsync(List<TaskLedgerEntry> ready)
    {
        var wave = ready
            .Take(Math.Max(1, policy.Policy.MaxConcurrency))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var task in wave)
        {
            var entry = progress.FindEntry(task.Id)!;
            entry.Status = SwarmStepStatus.Dispatched;
            entry.Attempts++;
            entry.DispatchedAt = now;
        }

        await PersistAsync();
        await MirrorProgressAsync(
            $"Run {policy.RunId} round {policy.Round}: dispatching {string.Join(", ", wave.Select(task => task.Id))}.");

        await Task.WhenAll(wave.Select(DispatchOneAsync));
        await PersistAsync();
        await MirrorProgressAsync(FormatProgressLine());
    }

    private async Task DispatchOneAsync(TaskLedgerEntry task)
    {
        var entry = progress.FindEntry(task.Id)!;
        try
        {
            entry.Status = SwarmStepStatus.InProgress;
            var request = new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = task.AssignedAgentHandle,
                MessageType = SurfaceSwarmMessageTypes.TaskDispatch,
                Kind = MessageKind.Request,
                Message = BuildWorkPackage(task, entry),
                Args = new Dictionary<string, string>
                {
                    [SurfaceSwarmArgs.TaskId] = task.Id,
                    [SurfaceSwarmArgs.RunId] = policy.RunId,
                    [SurfaceSwarmArgs.AgentName] = task.AssignedAgentName
                }
            };

            var response = await bus!.SendAndReceiveAsync(request, PerTaskTimeout());
            var output = response.Message ?? string.Empty;
            var artifact = new ArtifactEntry
            {
                TaskId = task.Id,
                Attempt = entry.Attempts,
                Output = Truncate(output, ArtifactEntry.OutputCap),
                CreatedAt = DateTimeOffset.UtcNow
            };
            artifacts.Entries.Add(artifact);

            entry.Status = SwarmStepStatus.PendingVerification;
            await VerifyAsync(task, entry, artifact);
        }
        catch (Exception ex)
        {
            supervisorLogger.LogWarning(
                ex,
                "Swarm task dispatch failed - Handle: {Handle}, RunId: {RunId}, TaskId: {TaskId}, AssignedAgent: {AssignedAgent}",
                fabrcoreAgentHost.GetHandle(),
                policy.RunId,
                task.Id,
                task.AssignedAgentHandle);
            entry.LastFailure = ex.Message;
            entry.Status = entry.Attempts < Math.Max(1, policy.Budgets.MaxTaskAttempts)
                ? SwarmStepStatus.Pending
                : SwarmStepStatus.Failed;
        }
    }

    private async Task VerifyAsync(TaskLedgerEntry task, ProgressEntry entry, ArtifactEntry artifact)
    {
        if (string.Equals(policy.Policy.VerificationDepth, "none", StringComparison.OrdinalIgnoreCase))
        {
            entry.Status = SwarmStepStatus.Completed;
            entry.VerifierFeedback = null;
            return;
        }

        SwarmVerdict verdict;
        try
        {
            var payload = new SwarmVerifyPayload
            {
                TaskId = task.Id,
                Description = task.Description,
                AcceptanceCriteria = [.. task.AcceptanceCriteria],
                Result = artifact.Output,
                VerificationDepth = policy.Policy.VerificationDepth
            };
            var response = await bus!.SendAndReceiveAsync(new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = runtime.Squad.VerifierHandle,
                MessageType = SurfaceSwarmMessageTypes.VerifyRequest,
                Kind = MessageKind.Request,
                Message = $"Verify task {task.Id} of run {policy.RunId}.",
                DataType = SurfaceSwarmDataTypes.Verify,
                Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(payload))
            }, PerTaskTimeout());

            verdict = ReadVerdict(response) ?? new SwarmVerdict
            {
                Pass = false,
                Reasons = ["The verifier verdict could not be parsed."]
            };
        }
        catch (Exception ex)
        {
            supervisorLogger.LogWarning(
                ex,
                "Swarm verification call failed - Handle: {Handle}, RunId: {RunId}, TaskId: {TaskId}",
                fabrcoreAgentHost.GetHandle(),
                policy.RunId,
                task.Id);
            verdict = new SwarmVerdict
            {
                Pass = false,
                Reasons = [$"The verifier was unreachable: {ex.Message}"]
            };
        }

        artifact.Verdict = verdict;
        if (verdict.Pass)
        {
            entry.Status = SwarmStepStatus.Completed;
            entry.VerifierFeedback = null;
            return;
        }

        entry.VerificationAttempts++;
        if (entry.VerificationAttempts < Math.Max(1, policy.Budgets.MaxValidationAttempts)
            && entry.Attempts < Math.Max(1, policy.Budgets.MaxTaskAttempts))
        {
            entry.Status = SwarmStepStatus.Pending;
            entry.VerifierFeedback = FormatVerifierFeedback(verdict);
        }
        else
        {
            entry.Status = SwarmStepStatus.Failed;
            entry.LastFailure = $"Verification failed: {string.Join("; ", verdict.Reasons)}";
        }
    }

    private async Task HandleStallAsync()
    {
        var failedCount = progress.Entries.Count(entry => entry.Status == SwarmStepStatus.Failed);
        var deadlocked = SurfaceSwarmDependencyResolver.HasDeadlock(taskLedger, progress, policy.Budgets);

        if ((failedCount >= Math.Max(1, policy.Policy.ReplanThreshold) || deadlocked)
            && SurfaceSwarmBudgetGuard.CanReplan(policy))
        {
            await ReplanAsync(BuildFailureSignal(failedCount, deadlocked), smeAdvice: null);
            return;
        }

        policy.ConsecutiveStalls++;
        if (SurfaceSwarmBudgetGuard.ShouldEscalate(policy))
        {
            var blocker = BuildFailureSignal(failedCount, deadlocked);
            var advice = await ConsultSmesAsync(blocker);
            if (advice.Count > 0 && SurfaceSwarmBudgetGuard.CanReplan(policy))
            {
                var adviceText = string.Join(Environment.NewLine, advice.Select(answer => $"{answer.SmeName}: {answer.Answer}"));
                await ReplanAsync(blocker, adviceText);
                return;
            }

            var note = advice.Count > 0
                ? $"{blocker}\nSME advice gathered:\n{string.Join(Environment.NewLine, advice.Select(answer => $"{answer.SmeName}: {answer.Answer}"))}"
                : blocker;
            await FinishRunAsync("blocked", escalate: true, escalationNote: note);
            return;
        }

        await PersistAsync();
        if (policy.IsRunning)
        {
            ScheduleTick(DriveLoopInterval());
        }
    }

    private async Task ReplanAsync(string failureSignal, string? smeAdvice)
    {
        policy.Replans++;
        policy.ConsecutiveStalls = 0;
        await PersistAsync();
        await MirrorProgressAsync($"Run {policy.RunId}: replanning ({policy.Replans}/{policy.Budgets.MaxReplans}) — {Truncate(failureSignal, 200)}");

        try
        {
            var context = new SwarmPlanningContext
            {
                Policy = policy.Policy,
                Budgets = policy.Budgets,
                PriorLedger = taskLedger,
                ProgressSummary = FormatProgressDetail(),
                FailureSignal = string.IsNullOrWhiteSpace(smeAdvice)
                    ? failureSignal
                    : $"{failureSignal}\n\nSME advice:\n{smeAdvice}"
            };
            var response = await bus!.SendAndReceiveAsync(new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = runtime.Squad.PlannerHandle,
                MessageType = SurfaceSwarmMessageTypes.PlanningRequest,
                Kind = MessageKind.Request,
                Message = taskLedger.Goal,
                DataType = SurfaceSwarmDataTypes.PlanningContext,
                Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(context))
            });

            var revised = ReadPayloadFromData<TaskLedger>(response.Data);
            if (revised is null || revised.Tasks.Count == 0)
            {
                supervisorLogger.LogWarning(
                    "Swarm replan produced no usable ledger - Handle: {Handle}, RunId: {RunId}",
                    fabrcoreAgentHost.GetHandle(),
                    policy.RunId);
                await FinishRunAsync("blocked", escalate: true,
                    escalationNote: $"{failureSignal}\nThe planner could not produce a recovery plan.");
                return;
            }

            MergeReplan(revised);
            await PersistAsync();
            await MirrorProgressAsync($"Run {policy.RunId}: revised ledger (revision {taskLedger.Revision}) with {taskLedger.Tasks.Count} tasks.");
            ScheduleTick(DriveLoopInterval());
        }
        catch (Exception ex)
        {
            supervisorLogger.LogWarning(
                ex,
                "Swarm replan request failed - Handle: {Handle}, RunId: {RunId}",
                fabrcoreAgentHost.GetHandle(),
                policy.RunId);
            await FinishRunAsync("blocked", escalate: true,
                escalationNote: $"{failureSignal}\nThe replan request failed: {ex.Message}");
        }
    }

    private void MergeReplan(TaskLedger revised)
    {
        var completedIds = progress.Entries
            .Where(entry => entry.Status is SwarmStepStatus.Completed or SwarmStepStatus.Skipped)
            .Select(entry => entry.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedTasks = taskLedger.Tasks
            .Where(task => completedIds.Contains(task.Id))
            .ToList();
        var newTasks = revised.Tasks
            .Where(task => !completedIds.Contains(task.Id))
            .ToList();

        taskLedger = new TaskLedger
        {
            Goal = string.IsNullOrWhiteSpace(revised.Goal) ? taskLedger.Goal : revised.Goal,
            Facts = revised.Facts.Count > 0 ? revised.Facts : taskLedger.Facts,
            Hypotheses = revised.Hypotheses.Count > 0 ? revised.Hypotheses : taskLedger.Hypotheses,
            Revision = Math.Max(revised.Revision, taskLedger.Revision + 1),
            Tasks = [.. completedTasks, .. newTasks]
        };

        progress = new ProgressLedger
        {
            Entries = taskLedger.Tasks.Select(task =>
            {
                var existing = progress.FindEntry(task.Id);
                if (existing is not null && completedIds.Contains(task.Id))
                {
                    return existing;
                }

                return new ProgressEntry { TaskId = task.Id };
            }).ToList()
        };
    }

    private async Task<List<SurfaceSwarmSmeAnswer>> ConsultSmesAsync(string blocker)
    {
        var consultant = new SurfaceSwarmSmeConsultant(
            bus!,
            runtime,
            fabrcoreAgentHost.GetHandle(),
            TimeSpan.FromSeconds(policy.Budgets.SmeConsultationTimeoutSeconds),
            supervisorLogger);
        if (consultant.Smes.Count == 0)
        {
            return [];
        }

        var question = $"""
            A Swarm squad run is blocked.

            Goal:
            {taskLedger.Goal}

            Blocker:
            {blocker}

            Progress:
            {FormatProgressDetail()}

            Provide concise, concrete guidance to unblock the run. If you cannot help, reply with "unknown".
            """;
        return await consultant.ConsultAllAsync(question);
    }

    private async Task FinishRunAsync(string outcome, bool escalate, string? escalationNote)
    {
        policy.IsRunning = false;
        policy.IsBlocked = !string.Equals(outcome, "completed", StringComparison.Ordinal);
        fabrcoreAgentHost.UnregisterTimer(SurfaceSwarmTimers.DriveLoop);
        await PersistAsync();

        var report = BuildFinalReport(outcome, escalationNote);
        await MirrorProgressAsync($"Run {policy.RunId} finished: {outcome}.");

        if (bus is not null)
        {
            await bus.SendAsync(new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = policy.CallerHandle,
                MessageType = SurfaceSwarmMessageTypes.Final,
                Kind = MessageKind.Request,
                Message = $"Run {policy.RunId} finished: {outcome}.",
                DataType = SurfaceSwarmDataTypes.Final,
                Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(report)),
                Args = new Dictionary<string, string>
                {
                    [SurfaceSwarmArgs.RunId] = policy.RunId
                }
            });

            if (escalate)
            {
                await bus.MirrorAsync(new AgentMessage
                {
                    FromHandle = fabrcoreAgentHost.GetHandle(),
                    ToHandle = runtime.Squad.PrincipalHandle,
                    MessageType = SurfaceSwarmMessageTypes.Escalation,
                    Kind = MessageKind.Response,
                    Message = escalationNote ?? $"Run {policy.RunId} needs human attention ({outcome})."
                }, SurfaceSwarmMessageTypes.Escalation);
            }
        }
    }

    private SwarmFinalReport BuildFinalReport(string outcome, string? escalationNote)
        => new()
        {
            RunId = policy.RunId,
            Outcome = outcome,
            Goal = taskLedger.Goal,
            EscalationNote = escalationNote,
            Tasks = taskLedger.Tasks.Select(task =>
            {
                var entry = progress.FindEntry(task.Id);
                var latest = artifacts.Entries.LastOrDefault(artifact =>
                    string.Equals(artifact.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
                return new SwarmFinalTaskSummary
                {
                    TaskId = task.Id,
                    Title = task.Title,
                    Status = entry?.Status ?? SwarmStepStatus.Pending,
                    // Full stored artifact output (already capped at ArtifactEntry.OutputCap)
                    // so the orchestrator synthesizes from complete results.
                    ResultSummary = latest?.Output,
                    FailureReason = entry?.LastFailure
                };
            }).ToList()
        };

    private string BuildWorkPackage(TaskLedgerEntry task, ProgressEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine(task.Description);
        sb.AppendLine();

        if (task.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("Acceptance criteria (your result will be verified against these):");
            foreach (var criterion in task.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(entry.VerifierFeedback))
        {
            sb.AppendLine("Verifier feedback from the previous attempt — address it explicitly:");
            sb.AppendLine(entry.VerifierFeedback);
            sb.AppendLine();
        }

        var dependencyContext = BuildDependencyContext(task);
        if (!string.IsNullOrWhiteSpace(dependencyContext))
        {
            sb.AppendLine("Context from completed prerequisite tasks:");
            sb.AppendLine(dependencyContext);
            sb.AppendLine();
        }

        sb.AppendLine("Complete the assigned task and return concrete results.");
        return sb.ToString();
    }

    private string BuildDependencyContext(TaskLedgerEntry task)
    {
        if (task.DependsOn.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var dep in task.DependsOn)
        {
            var latest = artifacts.Entries.LastOrDefault(artifact =>
                string.Equals(artifact.TaskId, dep, StringComparison.OrdinalIgnoreCase)
                && artifact.Verdict?.Pass != false);
            if (latest is not null)
            {
                sb.AppendLine($"[{dep}] {Truncate(latest.Output, DependencyContextCap)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private void ReclaimTimedOut()
    {
        var timedOut = SurfaceSwarmDependencyResolver.GetTimedOut(
            progress,
            DateTimeOffset.UtcNow,
            PerTaskTimeout() + PerTaskTimeout());
        foreach (var entry in timedOut)
        {
            entry.LastFailure = "The task timed out.";
            entry.Status = entry.Attempts < Math.Max(1, policy.Budgets.MaxTaskAttempts)
                ? SwarmStepStatus.Pending
                : SwarmStepStatus.Failed;
        }
    }

    private string BuildFailureSignal(int failedCount, bool deadlocked)
    {
        var failures = progress.Entries
            .Where(entry => entry.Status == SwarmStepStatus.Failed)
            .Select(entry => $"{entry.TaskId}: {entry.LastFailure ?? "unknown failure"}")
            .ToList();
        var reason = deadlocked && failedCount == 0
            ? "The remaining tasks have unresolvable dependencies."
            : $"{failedCount} task{(failedCount == 1 ? string.Empty : "s")} failed.";
        return failures.Count == 0
            ? reason
            : $"{reason}\n{string.Join(Environment.NewLine, failures)}";
    }

    private string FormatProgressLine()
    {
        var counts = Enum.GetValues<SwarmStepStatus>()
            .Select(status => (status, count: progress.Entries.Count(entry => entry.Status == status)))
            .Where(pair => pair.count > 0)
            .Select(pair => $"{pair.status}={pair.count}");
        return $"Run {policy.RunId} round {policy.Round}: {string.Join(", ", counts)}.";
    }

    private string FormatProgressDetail()
        => string.Join(Environment.NewLine, taskLedger.Tasks.Select(task =>
        {
            var entry = progress.FindEntry(task.Id);
            return $"- [{entry?.Status ?? SwarmStepStatus.Pending}] {task.Id} {task.Title} → {task.AssignedAgentName}"
                + (entry?.LastFailure is null ? string.Empty : $"; failure={entry.LastFailure}");
        }));

    private static string FormatVerifierFeedback(SwarmVerdict verdict)
    {
        var parts = new List<string>();
        if (verdict.MissingItems.Count > 0)
        {
            parts.Add($"Missing: {string.Join("; ", verdict.MissingItems)}");
        }

        if (!string.IsNullOrWhiteSpace(verdict.RetryGuidance))
        {
            parts.Add(verdict.RetryGuidance!);
        }

        if (parts.Count == 0 && verdict.Reasons.Count > 0)
        {
            parts.Add(string.Join("; ", verdict.Reasons));
        }

        return string.Join(Environment.NewLine, parts);
    }

    private async Task MirrorProgressAsync(string text)
    {
        SetStatusMessage(text);
        if (bus is null)
        {
            return;
        }

        // Progress mirrors ride as _status system messages so the transcript shows
        // them as transient activity instead of user-facing chat bubbles; the
        // swarm.progress marker stays in Args for tooling.
        await bus.MirrorAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.PrincipalHandle,
            MessageType = SystemMessageTypes.Status,
            Kind = MessageKind.Response,
            Message = text,
            Args = new Dictionary<string, string>
            {
                [SurfaceSwarmArgs.RunId] = policy.RunId,
                [SurfaceSwarmArgs.Progress] = "true"
            }
        }, SystemMessageTypes.Status);
    }

    private void ScheduleTick(TimeSpan dueTime)
        => fabrcoreAgentHost.RegisterTimer(
            SurfaceSwarmTimers.DriveLoop,
            SurfaceSwarmMessageTypes.Tick,
            "drive",
            dueTime,
            TimeSpan.Zero);

    private TimeSpan DriveLoopInterval()
        => TimeSpan.FromSeconds(Math.Max(1, policy.Budgets.DriveLoopIntervalSeconds));

    private TimeSpan PerTaskTimeout()
        => TimeSpan.FromSeconds(Math.Max(10, policy.Budgets.PerTaskTimeoutSeconds));

    private async Task PersistAsync()
    {
        SetState(TaskLedgerStateKey, taskLedger);
        SetState(ProgressLedgerStateKey, progress);
        SetState(ArtifactLedgerStateKey, artifacts);
        SetState(PolicyLedgerStateKey, policy);
        await FlushStateAsync();
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
        => statusSnapshot = policy.IsRunning
            ? FormatProgressLine()
            : policy.IsBlocked
                ? $"Run {policy.RunId} is blocked."
                : "Idle.";

    private async Task<T> LoadStateAsync<T>(string key)
        where T : class, new()
    {
        var stateRead = await TryGetStateAsync<T>(key);
        if (stateRead.Succeeded)
        {
            return stateRead.Value ?? new T();
        }

        supervisorLogger.LogWarning(
            stateRead.Error,
            "Swarm supervisor state could not be loaded and will be reset - Handle: {Handle}, StateKey: {StateKey}, ValueKind: {ValueKind}",
            fabrcoreAgentHost.GetHandle(),
            stateRead.Key,
            stateRead.ValueKind);

        RemoveState(stateRead.Key);
        await FlushStateAsync();
        return new T();
    }

    protected override Dictionary<string, string>? GetCustomHealthMetrics(HealthDetailLevel detailLevel)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["SwarmSquadName"] = runtime.Squad.Name,
            ["SwarmSquadHandle"] = runtime.Squad.OrchestratorHandle,
            ["SwarmRunId"] = policy.RunId,
            ["SwarmIsRunning"] = policy.IsRunning.ToString(),
            ["SwarmIsBlocked"] = policy.IsBlocked.ToString(),
            ["SwarmRound"] = policy.Round.ToString(),
            ["SwarmReplans"] = policy.Replans.ToString(),
            ["SwarmConsecutiveStalls"] = policy.ConsecutiveStalls.ToString(),
            ["SwarmTaskCount"] = taskLedger.Tasks.Count.ToString(),
            ["SwarmPendingCount"] = CountStatus(SwarmStepStatus.Pending).ToString(),
            ["SwarmInFlightCount"] = (CountStatus(SwarmStepStatus.Dispatched)
                + CountStatus(SwarmStepStatus.InProgress)
                + CountStatus(SwarmStepStatus.PendingVerification)).ToString(),
            ["SwarmCompletedCount"] = CountStatus(SwarmStepStatus.Completed).ToString(),
            ["SwarmFailedCount"] = CountStatus(SwarmStepStatus.Failed).ToString(),
            ["SwarmArtifactCount"] = artifacts.Entries.Count.ToString()
        };

    private int CountStatus(SwarmStepStatus status)
        => progress.Entries.Count(entry => entry.Status == status);

    private static T? ReadPayload<T>(AgentMessage message)
        where T : class
        => message.Data is { Length: > 0 } ? ReadPayloadFromData<T>(message.Data) : null;

    private static T? ReadPayloadFromData<T>(byte[]? data)
        where T : class
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(data), SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SwarmVerdict? ReadVerdict(AgentMessage message)
        => ReadPayloadFromData<SwarmVerdict>(message.Data);

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}
