using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Swarm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Tasks;

[AgentAlias(SurfaceSwarmAgentTypes.TaskRunner)]
[Description("Built-in task runner for Surface Task squads.")]
[FabrCoreCapabilities("Plans a user goal, delegates work to configured squad executors, retries with SME guidance, validates completion, and mirrors progress into the Surface squad.")]
public sealed class SurfaceTaskRunnerAgent : FabrCoreAgentProxy
{
    private const string StateKey = "surface-task-runner-state";
    private const string SelfChannel = "self";
    private const string TaskTickTimerName = "surface-task-runner-tick";

    private SurfaceSquadRuntime runtime = new();
    private SurfaceSquadConversationBus? bus;
    private IChatClient? plannerClient;
    private IChatClient? workerClient;
    private IFabrCoreRegistry? registry;
    private SurfaceTaskRunState state = new();
    private readonly ILogger<SurfaceTaskRunnerAgent> taskLogger;

    public SurfaceTaskRunnerAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        taskLogger = loggerFactory.CreateLogger<SurfaceTaskRunnerAgent>();
    }

    public override async Task OnInitialize()
    {
        var handle = fabrcoreAgentHost.GetHandle();
        taskLogger.LogInformation(
            "Surface Task runner initializing - Handle: {Handle}, AgentType: {AgentType}, ConfigHandle: {ConfigHandle}, Models: {Models}, HasSquadDefinition: {HasSquadDefinition}, ArgKeys: {ArgKeys}",
            handle,
            config.AgentType,
            config.Handle,
            config.Models,
            config.Args.ContainsKey(SurfaceSquadArgs.SquadDefinition),
            string.Join(", ", config.Args.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));

        runtime = SurfaceSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        bus = new SurfaceSquadConversationBus(fabrcoreAgentHost, runtime);
        registry = serviceProvider.GetService<IFabrCoreRegistry>();

        var options = runtime.Squad.TaskOptions;
        var plannerModel = BlankToDefault(options.PlannerModelName);
        var workerModel = BlankToDefault(options.WorkerModelName);

        taskLogger.LogInformation(
            "Surface Task runtime loaded - Handle: {Handle}, Squad: {SquadName}, SquadHandle: {SquadHandle}, Type: {SquadType}, Agents: {AgentCount}, Executors: {ExecutorCount}, SMEs: {SmeCount}, Helpers: {HelperCount}, PlannerModel: {PlannerModel}, WorkerModel: {WorkerModel}",
            handle,
            runtime.Squad.Name,
            runtime.Squad.OrchestratorHandle,
            runtime.Squad.SquadType,
            runtime.Squad.Agents.Count,
            CountRole(SurfaceSquadMemberRole.Executor),
            CountRole(SurfaceSquadMemberRole.SubjectMatterExpert),
            CountRole(SurfaceSquadMemberRole.Helper),
            plannerModel,
            workerModel);

        try
        {
            taskLogger.LogDebug("Resolving Surface Task planner chat client - Handle: {Handle}, Model: {Model}", handle, plannerModel);
            plannerClient = await GetChatClient(plannerModel);
            taskLogger.LogInformation("Surface Task planner chat client resolved - Handle: {Handle}, Model: {Model}", handle, plannerModel);
        }
        catch (Exception ex)
        {
            taskLogger.LogError(ex, "Failed to resolve Surface Task planner chat client - Handle: {Handle}, Model: {Model}", handle, plannerModel);
            throw;
        }

        try
        {
            taskLogger.LogDebug("Resolving Surface Task worker chat client - Handle: {Handle}, Model: {Model}", handle, workerModel);
            workerClient = await GetChatClient(workerModel);
            taskLogger.LogInformation("Surface Task worker chat client resolved - Handle: {Handle}, Model: {Model}", handle, workerModel);
        }
        catch (Exception ex)
        {
            taskLogger.LogError(ex, "Failed to resolve Surface Task worker chat client - Handle: {Handle}, Model: {Model}", handle, workerModel);
            throw;
        }

        state = await LoadStateAsync();
        taskLogger.LogInformation(
            "Surface Task state loaded - Handle: {Handle}, IsRunning: {IsRunning}, IsBlocked: {IsBlocked}, TaskCount: {TaskCount}, Pending: {PendingCount}, InProgress: {InProgressCount}, Completed: {CompletedCount}, Failed: {FailedCount}, ValidationAttempts: {ValidationAttempts}",
            handle,
            state.IsRunning,
            state.IsBlocked,
            state.Tasks.Count,
            CountTasks(SurfaceTaskItemStatus.Pending),
            CountTasks(SurfaceTaskItemStatus.InProgress),
            CountTasks(SurfaceTaskItemStatus.Completed),
            CountTasks(SurfaceTaskItemStatus.Failed),
            state.ValidationAttempts);

        if (state.IsRunning && state.Tasks.Any(task => task.Status == SurfaceTaskItemStatus.Pending))
        {
            taskLogger.LogInformation("Surface Task runner restored with pending work; scheduling continuation tick - Handle: {Handle}", handle);
            ScheduleTaskTick();
        }
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        taskLogger.LogInformation(
            "Surface Task runner received message - Handle: {Handle}, From: {FromHandle}, To: {ToHandle}, MessageType: {MessageType}, MessageChannel: {MessageChannel}, Kind: {Kind}, TextLength: {TextLength}, IsRunning: {IsRunning}, PlannerReady: {PlannerReady}, WorkerReady: {WorkerReady}, BusReady: {BusReady}",
            fabrcoreAgentHost.GetHandle(),
            message.FromHandle,
            message.ToHandle,
            message.MessageType,
            message.Channel,
            message.Kind,
            message.Message?.Length ?? 0,
            state.IsRunning,
            plannerClient is not null,
            workerClient is not null,
            bus is not null);

        if (message.MessageType == SurfaceSquadMessageTypes.TaskTick || message.Channel == SelfChannel)
        {
            fabrcoreAgentHost.UnregisterTimer(TaskTickTimerName);
            taskLogger.LogDebug("Surface Task runner processing scheduled tick - Handle: {Handle}", fabrcoreAgentHost.GetHandle());
            await ProcessNextTaskAsync();
            return message.Response();
        }

        var response = message.Response();
        Stamp(response);

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            taskLogger.LogDebug("Surface Task runner received empty chat message - Handle: {Handle}", fabrcoreAgentHost.GetHandle());
            response.Message = "Send a goal for this task squad.";
            return response;
        }

        if (plannerClient is null || workerClient is null || bus is null)
        {
            taskLogger.LogWarning(
                "Surface Task runner not initialized during chat - Handle: {Handle}, PlannerReady: {PlannerReady}, WorkerReady: {WorkerReady}, BusReady: {BusReady}, Squad: {SquadName}, SquadHandle: {SquadHandle}, AgentCount: {AgentCount}",
                fabrcoreAgentHost.GetHandle(),
                plannerClient is not null,
                workerClient is not null,
                bus is not null,
                runtime.Squad.Name,
                runtime.Squad.OrchestratorHandle,
                runtime.Squad.Agents.Count);
            response.Message = "Task runner is not initialized.";
            return response;
        }

        if (state.IsRunning)
        {
            taskLogger.LogInformation(
                "Surface Task runner rejected new goal because active run is in progress - Handle: {Handle}, TaskCount: {TaskCount}, Pending: {PendingCount}, InProgress: {InProgressCount}",
                fabrcoreAgentHost.GetHandle(),
                state.Tasks.Count,
                CountTasks(SurfaceTaskItemStatus.Pending),
                CountTasks(SurfaceTaskItemStatus.InProgress));
            response.Message = "This task squad is already working. I will finish the active task before starting a new goal.";
            return response;
        }

        var executors = runtime.Squad.Agents
            .Where(agent => agent.Role == SurfaceSquadMemberRole.Executor)
            .ToList();
        if (executors.Count == 0)
        {
            taskLogger.LogWarning(
                "Surface Task runner cannot start goal because no executors are configured - Handle: {Handle}, Squad: {SquadName}, AgentCount: {AgentCount}, AgentRoles: {AgentRoles}",
                fabrcoreAgentHost.GetHandle(),
                runtime.Squad.Name,
                runtime.Squad.Agents.Count,
                FormatAgentRoles());
            response.Message = "Add at least one executor agent before starting a Task squad goal.";
            return response;
        }

        taskLogger.LogInformation(
            "Surface Task runner building task plan - Handle: {Handle}, Squad: {SquadName}, GoalPreview: {GoalPreview}, ExecutorCount: {ExecutorCount}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.Name,
            Truncate(message.Message, 160),
            executors.Count);
        var capabilities = await BuildCapabilitiesAsync();
        var plan = await BuildPlanAsync(message.Message!, capabilities);
        if (plan.Tasks.Count == 0)
        {
            taskLogger.LogWarning(
                "Surface Task planner returned no tasks - Handle: {Handle}, Squad: {SquadName}, CapabilityCount: {CapabilityCount}, GoalPreview: {GoalPreview}",
                fabrcoreAgentHost.GetHandle(),
                runtime.Squad.Name,
                capabilities.Count,
                Truncate(message.Message, 160));
            response.Message = "I could not build a task plan from that goal. Try adding more detail or another executor agent.";
            return response;
        }

        state = new SurfaceTaskRunState
        {
            Goal = message.Message!,
            CallerHandle = message.FromHandle ?? runtime.Squad.PrincipalHandle,
            IsRunning = true,
            Tasks = plan.Tasks.Select((task, index) => new SurfaceTaskItem
            {
                Order = index + 1,
                Description = task.Description,
                AssignedAgent = ResolveExecutor(task.AgentName, executors).Handle,
                AssignedAgentName = ResolveExecutor(task.AgentName, executors).Name,
                MaxAttempts = Math.Max(1, runtime.Squad.TaskOptions.MaxTaskAttempts)
            }).ToList()
        };

        taskLogger.LogInformation(
            "Surface Task runner started plan - Handle: {Handle}, GoalPreview: {GoalPreview}, TaskCount: {TaskCount}, Assignments: {Assignments}",
            fabrcoreAgentHost.GetHandle(),
            Truncate(state.Goal, 160),
            state.Tasks.Count,
            string.Join("; ", state.Tasks.Select(task => $"{task.Order}:{task.AssignedAgentName}->{task.AssignedAgent}")));

        await PersistAsync();
        await SendStatusAsync($"Started task plan with {state.Tasks.Count} step{(state.Tasks.Count == 1 ? string.Empty : "s")}.");
        ScheduleTaskTick();

        response.Message = $"Started working on: {state.Goal}\n\nI will update this squad as tasks complete.";
        return response;
    }

    private async Task ProcessNextTaskAsync()
    {
        if (!state.IsRunning || bus is null)
        {
            taskLogger.LogDebug(
                "Surface Task scheduled tick ignored - Handle: {Handle}, IsRunning: {IsRunning}, BusReady: {BusReady}",
                fabrcoreAgentHost.GetHandle(),
                state.IsRunning,
                bus is not null);
            return;
        }

        var next = state.Tasks
            .Where(task => task.Status == SurfaceTaskItemStatus.Pending)
            .OrderBy(task => task.Order)
            .FirstOrDefault();

        if (next is null)
        {
            taskLogger.LogInformation(
                "Surface Task runner found no pending steps; validating completion - Handle: {Handle}, TaskCount: {TaskCount}, Completed: {CompletedCount}, Failed: {FailedCount}",
                fabrcoreAgentHost.GetHandle(),
                state.Tasks.Count,
                CountTasks(SurfaceTaskItemStatus.Completed),
                CountTasks(SurfaceTaskItemStatus.Failed));
            await ValidateAndCompleteAsync();
            return;
        }

        taskLogger.LogInformation(
            "Surface Task runner starting step - Handle: {Handle}, Step: {Step}, Attempt: {Attempt}, MaxAttempts: {MaxAttempts}, AssignedAgent: {AssignedAgent}, AssignedAgentName: {AssignedAgentName}, Description: {Description}",
            fabrcoreAgentHost.GetHandle(),
            next.Order,
            next.AttemptCount + 1,
            next.MaxAttempts,
            next.AssignedAgent,
            next.AssignedAgentName,
            Truncate(next.Description, 240));
        next.Status = SurfaceTaskItemStatus.InProgress;
        next.AttemptCount++;
        await PersistAsync();
        await SendStatusAsync($"Running step {next.Order}: {next.Description}");

        var result = await DelegateAsync(next);
        if (result.Success)
        {
            taskLogger.LogInformation(
                "Surface Task runner completed step - Handle: {Handle}, Step: {Step}, AssignedAgent: {AssignedAgent}, Summary: {Summary}, WarningCount: {WarningCount}",
                fabrcoreAgentHost.GetHandle(),
                next.Order,
                next.AssignedAgent,
                Truncate(result.Summary, 240),
                result.Warnings.Count);
            next.Status = SurfaceTaskItemStatus.Completed;
            next.Result = result.ProseText;
            next.Warnings = result.Warnings;
            await PersistAsync();
            await SendStatusAsync($"Completed step {next.Order}: {result.Summary}");
            ScheduleTaskTick();
            return;
        }

        taskLogger.LogWarning(
            "Surface Task runner step delegation failed - Handle: {Handle}, Step: {Step}, AssignedAgent: {AssignedAgent}, Attempt: {Attempt}, FailureReason: {FailureReason}",
            fabrcoreAgentHost.GetHandle(),
            next.Order,
            next.AssignedAgent,
            next.AttemptCount,
            Truncate(result.FailureReason, 240));
        var smeGuidance = await ConsultSmesAsync(next, result.FailureReason ?? "The delegated agent could not complete the task.");
        if (!string.IsNullOrWhiteSpace(smeGuidance) && next.AttemptCount < next.MaxAttempts)
        {
            taskLogger.LogInformation(
                "Surface Task runner will retry step with SME guidance - Handle: {Handle}, Step: {Step}, Attempt: {Attempt}, GuidancePreview: {GuidancePreview}",
                fabrcoreAgentHost.GetHandle(),
                next.Order,
                next.AttemptCount,
                Truncate(smeGuidance, 240));
            next.Status = SurfaceTaskItemStatus.Pending;
            next.RoadblockNote = smeGuidance;
            await PersistAsync();
            await SendStatusAsync($"Retrying step {next.Order} with SME guidance.");
            ScheduleTaskTick();
            return;
        }

        next.Status = SurfaceTaskItemStatus.Failed;
        next.Result = result.ProseText;
        next.FailureReason = result.FailureReason;
        taskLogger.LogWarning(
            "Surface Task runner marked step failed and will replan or block - Handle: {Handle}, Step: {Step}, AssignedAgent: {AssignedAgent}, FailureReason: {FailureReason}",
            fabrcoreAgentHost.GetHandle(),
            next.Order,
            next.AssignedAgent,
            Truncate(next.FailureReason, 240));
        await PersistAsync();
        await ReplanOrBlockAsync(result.FailureReason ?? "A task failed after retries.");
    }

    private async Task<SurfaceTaskPlanDraft> BuildPlanAsync(
        string goal,
        IReadOnlyList<SurfaceSquadAgentCapability> capabilities)
    {
        taskLogger.LogDebug(
            "Surface Task planner request starting - Handle: {Handle}, Squad: {SquadName}, CapabilityCount: {CapabilityCount}, GoalLength: {GoalLength}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.Name,
            capabilities.Count,
            goal.Length);

        var prompt = $$"""
            Build a sequential task plan for the Surface Task squad "{{runtime.Squad.Name}}".
            Return only JSON with this shape:
            {"tasks":[{"description":"task instruction","agentName":"executor name or handle"}]}

            Goal:
            {{goal}}

            Squad guidance:
            {{runtime.Squad.TaskOptions.PersonaPrompt}}

            Available agents:
            {{FormatCapabilities(capabilities)}}

            Use only agents with role Executor for executable tasks.
            Keep the plan short and ordered.
            """;

        var result = await plannerClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var text = result.Text ?? string.Empty;
        var plan = SurfaceTaskPlanDraft.Parse(text);
        if (plan is null)
        {
            taskLogger.LogWarning(
                "Surface Task planner response could not be parsed - Handle: {Handle}, Squad: {SquadName}, ResponseLength: {ResponseLength}, ResponsePreview: {ResponsePreview}",
                fabrcoreAgentHost.GetHandle(),
                runtime.Squad.Name,
                text.Length,
                Truncate(text, 500));
            return new SurfaceTaskPlanDraft();
        }

        taskLogger.LogInformation(
            "Surface Task planner response parsed - Handle: {Handle}, Squad: {SquadName}, TaskCount: {TaskCount}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.Name,
            plan.Tasks.Count);
        return plan;
    }

    private async Task<SurfaceTaskDelegationResult> DelegateAsync(SurfaceTaskItem task)
    {
        try
        {
            var prompt = BuildDelegationPrompt(task);
            taskLogger.LogInformation(
                "Surface Task delegating step - Handle: {Handle}, Step: {Step}, To: {ToHandle}, AgentName: {AgentName}, PromptLength: {PromptLength}",
                fabrcoreAgentHost.GetHandle(),
                task.Order,
                task.AssignedAgent,
                task.AssignedAgentName,
                prompt.Length);
            var request = new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = task.AssignedAgent,
                MessageType = SurfaceSquadMessageTypes.TaskDelegation,
                Kind = MessageKind.Request,
                Message = prompt,
                State = new Dictionary<string, string>
                {
                    [SurfaceSquadArgs.SquadHandle] = runtime.Squad.OrchestratorHandle,
                    [SurfaceSquadArgs.SquadName] = runtime.Squad.Name,
                    [SurfaceSquadArgs.AgentName] = task.AssignedAgentName
                }
            };

            var response = await bus!.SendAndReceiveAsync(request);
            var result = SurfaceTaskDelegationResult.FromMessage(response.Message);
            taskLogger.LogInformation(
                "Surface Task delegation response received - Handle: {Handle}, Step: {Step}, From: {FromHandle}, Success: {Success}, Summary: {Summary}, ResponseLength: {ResponseLength}",
                fabrcoreAgentHost.GetHandle(),
                task.Order,
                response.FromHandle,
                result.Success,
                Truncate(result.Summary, 240),
                response.Message?.Length ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            taskLogger.LogWarning(
                ex,
                "Surface Task delegation threw - Handle: {Handle}, Step: {Step}, To: {ToHandle}, AgentName: {AgentName}",
                fabrcoreAgentHost.GetHandle(),
                task.Order,
                task.AssignedAgent,
                task.AssignedAgentName);
            return new SurfaceTaskDelegationResult(false, string.Empty, [], "failed", ex.Message);
        }
    }

    private string BuildDelegationPrompt(SurfaceTaskItem task)
    {
        var sb = new StringBuilder();
        sb.AppendLine(task.Description);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(task.RoadblockNote))
        {
            sb.AppendLine("SME guidance for this retry:");
            sb.AppendLine(task.RoadblockNote);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(runtime.Squad.TaskOptions.ClientAgentOverlay))
        {
            sb.AppendLine(runtime.Squad.TaskOptions.ClientAgentOverlay);
        }
        else
        {
            sb.AppendLine("Complete the assigned task and return concrete results.");
        }

        sb.AppendLine();
        sb.AppendLine("End your reply with this optional fenced envelope:");
        sb.AppendLine("```fabrcore-envelope");
        sb.AppendLine("""
            {
              "status": "completed|failed|partial",
              "summary": "<one-line outcome>",
              "warnings": []
            }
            """);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private async Task<string?> ConsultSmesAsync(SurfaceTaskItem task, string failureReason)
    {
        var smes = runtime.Squad.Agents
            .Where(agent => agent.Role == SurfaceSquadMemberRole.SubjectMatterExpert)
            .ToList();
        if (smes.Count == 0)
        {
            taskLogger.LogDebug(
                "Surface Task has no SMEs for failed step - Handle: {Handle}, Step: {Step}",
                fabrcoreAgentHost.GetHandle(),
                task.Order);
            return null;
        }

        taskLogger.LogInformation(
            "Surface Task consulting SMEs - Handle: {Handle}, Step: {Step}, SmeCount: {SmeCount}, FailureReason: {FailureReason}",
            fabrcoreAgentHost.GetHandle(),
            task.Order,
            smes.Count,
            Truncate(failureReason, 240));

        var question = $"""
            A Surface Task squad step failed.

            Goal:
            {state.Goal}

            Failed step:
            {task.Description}

            Failure:
            {failureReason}

            Provide concise retry guidance. If you cannot help, reply with "unknown".
            """;

        foreach (var sme in smes)
        {
            try
            {
                taskLogger.LogDebug(
                    "Surface Task consulting SME - Handle: {Handle}, Step: {Step}, SmeHandle: {SmeHandle}, SmeName: {SmeName}",
                    fabrcoreAgentHost.GetHandle(),
                    task.Order,
                    sme.Handle,
                    sme.Name);
                var response = await bus!.SendAndReceiveAsync(new AgentMessage
                {
                    FromHandle = fabrcoreAgentHost.GetHandle(),
                    ToHandle = sme.Handle,
                    MessageType = SurfaceSquadMessageTypes.SmeConsultation,
                    Kind = MessageKind.Request,
                    Message = question,
                    Args = new Dictionary<string, string>
                    {
                        [SurfaceSquadArgs.AgentName] = sme.Name
                    }
                });

                var text = response.Message?.Trim();
                if (!string.IsNullOrWhiteSpace(text)
                    && !text.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    taskLogger.LogInformation(
                        "Surface Task SME returned retry guidance - Handle: {Handle}, Step: {Step}, SmeHandle: {SmeHandle}, GuidancePreview: {GuidancePreview}",
                        fabrcoreAgentHost.GetHandle(),
                        task.Order,
                        sme.Handle,
                        Truncate(text, 240));
                    return $"{sme.Name}: {text}";
                }

                taskLogger.LogDebug(
                    "Surface Task SME returned no usable guidance - Handle: {Handle}, Step: {Step}, SmeHandle: {SmeHandle}, ResponsePreview: {ResponsePreview}",
                    fabrcoreAgentHost.GetHandle(),
                    task.Order,
                    sme.Handle,
                    Truncate(text, 240));
            }
            catch (Exception ex)
            {
                // Try the next SME; one slow or unavailable SME should not block the channel.
                taskLogger.LogWarning(
                    ex,
                    "Surface Task SME consultation failed - Handle: {Handle}, Step: {Step}, SmeHandle: {SmeHandle}",
                    fabrcoreAgentHost.GetHandle(),
                    task.Order,
                    sme.Handle);
            }
        }

        taskLogger.LogInformation(
            "Surface Task SME consultation produced no retry guidance - Handle: {Handle}, Step: {Step}",
            fabrcoreAgentHost.GetHandle(),
            task.Order);
        return null;
    }

    private async Task ReplanOrBlockAsync(string signal)
    {
        var capabilities = await BuildCapabilitiesAsync();
        taskLogger.LogInformation(
            "Surface Task requesting recovery plan - Handle: {Handle}, Signal: {Signal}, CapabilityCount: {CapabilityCount}",
            fabrcoreAgentHost.GetHandle(),
            Truncate(signal, 240),
            capabilities.Count);
        var prompt = $$"""
            A Surface Task squad plan hit a failure.
            Return only JSON with this shape:
            {"tasks":[{"description":"task instruction","agentName":"executor name or handle"}]}
            Return an empty tasks array if no available executor can address the failure.

            Original goal:
            {{state.Goal}}

            Failure signal:
            {{signal}}

            Current task states:
            {{FormatTaskState()}}

            Available agents:
            {{FormatCapabilities(capabilities)}}
            """;

        var result = await plannerClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var text = result.Text ?? string.Empty;
        var plan = SurfaceTaskPlanDraft.Parse(text);
        if (plan is null)
        {
            taskLogger.LogWarning(
                "Surface Task recovery planner response could not be parsed - Handle: {Handle}, ResponseLength: {ResponseLength}, ResponsePreview: {ResponsePreview}",
                fabrcoreAgentHost.GetHandle(),
                text.Length,
                Truncate(text, 500));
        }

        if (plan?.Tasks.Count > 0)
        {
            var executors = runtime.Squad.Agents
                .Where(agent => agent.Role == SurfaceSquadMemberRole.Executor)
                .ToList();
            var nextOrder = state.Tasks.Count == 0 ? 1 : state.Tasks.Max(task => task.Order) + 1;
            foreach (var task in plan.Tasks)
            {
                var executor = ResolveExecutor(task.AgentName, executors);
                state.Tasks.Add(new SurfaceTaskItem
                {
                    Order = nextOrder++,
                    Description = task.Description,
                    AssignedAgent = executor.Handle,
                    AssignedAgentName = executor.Name,
                    MaxAttempts = Math.Max(1, runtime.Squad.TaskOptions.MaxTaskAttempts)
                });
            }

            taskLogger.LogInformation(
                "Surface Task added recovery tasks - Handle: {Handle}, AddedTaskCount: {AddedTaskCount}, TotalTaskCount: {TotalTaskCount}",
                fabrcoreAgentHost.GetHandle(),
                plan.Tasks.Count,
                state.Tasks.Count);
            await PersistAsync();
            await SendStatusAsync("Added recovery tasks after a failed step.");
            ScheduleTaskTick();
            return;
        }

        taskLogger.LogWarning(
            "Surface Task is blocked; recovery planner returned no tasks - Handle: {Handle}, Signal: {Signal}",
            fabrcoreAgentHost.GetHandle(),
            Truncate(signal, 240));
        state.IsRunning = false;
        state.IsBlocked = true;
        state.FinalResult = $"The task is blocked: {signal}";
        await PersistAsync();
        await SendFinalAsync(state.FinalResult);
    }

    private async Task ValidateAndCompleteAsync()
    {
        taskLogger.LogInformation(
            "Surface Task validating completion - Handle: {Handle}, TaskCount: {TaskCount}, Completed: {CompletedCount}, Failed: {FailedCount}, ValidationAttempts: {ValidationAttempts}",
            fabrcoreAgentHost.GetHandle(),
            state.Tasks.Count,
            CountTasks(SurfaceTaskItemStatus.Completed),
            CountTasks(SurfaceTaskItemStatus.Failed),
            state.ValidationAttempts);

        var prompt = $$"""
            Validate whether the completed task results satisfy the user's goal.
            Return only JSON:
            {"isSatisfied":true|false,"summary":"short explanation","missing":["gap"]}

            Goal:
            {{state.Goal}}

            Task results:
            {{FormatTaskState()}}
            """;

        var result = await plannerClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var text = result.Text ?? string.Empty;
        var validation = SurfaceTaskValidationResult.Parse(text);
        if (validation is null)
        {
            taskLogger.LogWarning(
                "Surface Task validation response could not be parsed; defaulting to satisfied - Handle: {Handle}, ResponseLength: {ResponseLength}, ResponsePreview: {ResponsePreview}",
                fabrcoreAgentHost.GetHandle(),
                text.Length,
                Truncate(text, 500));
            validation = new SurfaceTaskValidationResult { IsSatisfied = true, Summary = "Completed." };
        }

        taskLogger.LogInformation(
            "Surface Task validation parsed - Handle: {Handle}, IsSatisfied: {IsSatisfied}, Summary: {Summary}, MissingCount: {MissingCount}",
            fabrcoreAgentHost.GetHandle(),
            validation.IsSatisfied,
            Truncate(validation.Summary, 240),
            validation.Missing.Count);

        state.ValidationAttempts++;
        if (!validation.IsSatisfied
            && state.ValidationAttempts < Math.Max(1, runtime.Squad.TaskOptions.MaxValidationAttempts))
        {
            taskLogger.LogInformation(
                "Surface Task validation was not satisfied; requesting replan - Handle: {Handle}, ValidationAttempts: {ValidationAttempts}, MaxValidationAttempts: {MaxValidationAttempts}",
                fabrcoreAgentHost.GetHandle(),
                state.ValidationAttempts,
                Math.Max(1, runtime.Squad.TaskOptions.MaxValidationAttempts));
            await ReplanOrBlockAsync(validation.Summary);
            return;
        }

        var final = await SynthesizeFinalAsync(validation);
        state.IsRunning = false;
        state.IsBlocked = !validation.IsSatisfied;
        state.FinalResult = final;
        taskLogger.LogInformation(
            "Surface Task run finalized - Handle: {Handle}, IsBlocked: {IsBlocked}, FinalLength: {FinalLength}",
            fabrcoreAgentHost.GetHandle(),
            state.IsBlocked,
            final.Length);
        await PersistAsync();
        await SendFinalAsync(final);
    }

    private async Task<string> SynthesizeFinalAsync(SurfaceTaskValidationResult validation)
    {
        taskLogger.LogDebug(
            "Surface Task synthesizing final response - Handle: {Handle}, IsSatisfied: {IsSatisfied}",
            fabrcoreAgentHost.GetHandle(),
            validation.IsSatisfied);

        var prompt = $$"""
            Write the final response for a Surface Task squad.
            Be concise, include the outcome, and mention blockers if the goal is not fully satisfied.

            Goal:
            {{state.Goal}}

            Validation:
            {{validation.Summary}}

            Task results:
            {{FormatTaskState()}}
            """;

        var result = await workerClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var final = string.IsNullOrWhiteSpace(result.Text) ? validation.Summary : result.Text!;
        taskLogger.LogInformation(
            "Surface Task final response synthesized - Handle: {Handle}, UsedValidationSummaryFallback: {UsedFallback}, ResponseLength: {ResponseLength}",
            fabrcoreAgentHost.GetHandle(),
            string.IsNullOrWhiteSpace(result.Text),
            final.Length);
        return final;
    }

    private async Task<List<SurfaceSquadAgentCapability>> BuildCapabilitiesAsync()
    {
        taskLogger.LogDebug(
            "Surface Task building squad capabilities - Handle: {Handle}, Squad: {SquadName}, AgentCount: {AgentCount}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.Name,
            runtime.Squad.Agents.Count);

        var capabilities = new List<SurfaceSquadAgentCapability>();
        foreach (var squadAgent in runtime.Squad.Agents)
        {
            RegistryEntry? registryEntry = null;
            try
            {
                registryEntry = registry?.GetAgentTypes()
                    .FirstOrDefault(entry => entry.Aliases.Any(alias =>
                        string.Equals(alias, squadAgent.AgentType, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(alias, ShortHandle(squadAgent.Handle), StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                taskLogger.LogDebug(
                    ex,
                    "Surface Task registry lookup failed for squad agent - Handle: {Handle}, AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    fabrcoreAgentHost.GetHandle(),
                    squadAgent.Handle,
                    squadAgent.AgentType);
                registryEntry = null;
            }

            AgentHealthStatus? health = null;
            string? unavailableReason = null;
            try
            {
                health = await fabrcoreAgentHost.GetAgentHealth(squadAgent.Handle, HealthDetailLevel.Detailed);
                if (health?.IsConfigured != true)
                {
                    unavailableReason = "Agent is not configured.";
                }
                taskLogger.LogDebug(
                    "Surface Task squad agent health checked - Handle: {Handle}, AgentHandle: {AgentHandle}, AgentType: {AgentType}, State: {State}, IsConfigured: {IsConfigured}",
                    fabrcoreAgentHost.GetHandle(),
                    squadAgent.Handle,
                    squadAgent.AgentType,
                    health?.State,
                    health?.IsConfigured);
            }
            catch (Exception ex)
            {
                unavailableReason = ex.Message;
                taskLogger.LogWarning(
                    ex,
                    "Surface Task squad agent health check failed - Handle: {Handle}, AgentHandle: {AgentHandle}, AgentType: {AgentType}",
                    fabrcoreAgentHost.GetHandle(),
                    squadAgent.Handle,
                    squadAgent.AgentType);
            }

            var capability = SurfaceSquadAgentCapabilityProjection.Build(
                squadAgent,
                registryEntry,
                health,
                unavailableReason);
            capability.Notes = string.IsNullOrWhiteSpace(capability.Notes)
                ? $"Role: {squadAgent.Role}"
                : $"{capability.Notes}{Environment.NewLine}Role: {squadAgent.Role}";
            capabilities.Add(capability);
        }

        taskLogger.LogInformation(
            "Surface Task capabilities built - Handle: {Handle}, CapabilityCount: {CapabilityCount}, ConfiguredCount: {ConfiguredCount}, UnavailableCount: {UnavailableCount}",
            fabrcoreAgentHost.GetHandle(),
            capabilities.Count,
            capabilities.Count(capability => capability.IsConfigured),
            capabilities.Count(capability => !capability.IsConfigured));
        return capabilities;
    }

    private static string FormatCapabilities(IEnumerable<SurfaceSquadAgentCapability> capabilities)
        => string.Join(Environment.NewLine + Environment.NewLine, capabilities.Select(capability =>
            $"""
            - name: {capability.Name}
              handle: {capability.Handle}
              type: {capability.AgentType}
              status: {(capability.IsConfigured ? "configured" : $"unavailable: {capability.UnavailableReason ?? "not configured"}")}
              description: {capability.Description}
              plugins: {string.Join(", ", capability.Plugins)}
              tools: {string.Join(", ", capability.Tools)}
              notes: {capability.Notes}
            """));

    private string FormatTaskState()
        => string.Join(Environment.NewLine, state.Tasks
            .OrderBy(task => task.Order)
            .Select(task =>
                $"- [{task.Status}] step {task.Order} assigned to {task.AssignedAgentName}: {task.Description}; result={task.Result}; failure={task.FailureReason}; warnings={string.Join(", ", task.Warnings)}"));

    private SurfaceSquadAgent ResolveExecutor(string? requested, IReadOnlyList<SurfaceSquadAgent> executors)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = executors.FirstOrDefault(agent =>
                string.Equals(agent.Name, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(agent.Handle, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ShortHandle(agent.Handle), requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(agent.AgentType, requested, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return executors.First();
    }

    private async Task SendStatusAsync(string text)
    {
        taskLogger.LogDebug(
            "Surface Task sending status - Handle: {Handle}, SquadOwner: {PrincipalHandle}, Status: {Status}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.PrincipalHandle,
            Truncate(text, 240));
        fabrcoreAgentHost.SetStatusMessage(text);
        await bus!.MirrorAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.PrincipalHandle,
            MessageType = SystemMessageTypes.Status,
            Kind = MessageKind.Response,
            Message = text
        }, SystemMessageTypes.Status);
    }

    private async Task SendFinalAsync(string text)
    {
        taskLogger.LogInformation(
            "Surface Task sending final response - Handle: {Handle}, SquadOwner: {PrincipalHandle}, ResponseLength: {ResponseLength}",
            fabrcoreAgentHost.GetHandle(),
            runtime.Squad.PrincipalHandle,
            text.Length);
        await bus!.MirrorAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.PrincipalHandle,
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Response,
            Message = text
        }, SurfaceSquadMessageTypes.Chat);
    }

    private void ScheduleTaskTick()
    {
        taskLogger.LogDebug(
            "Surface Task scheduling continuation tick - Handle: {Handle}, Pending: {PendingCount}, InProgress: {InProgressCount}",
            fabrcoreAgentHost.GetHandle(),
            CountTasks(SurfaceTaskItemStatus.Pending),
            CountTasks(SurfaceTaskItemStatus.InProgress));
        fabrcoreAgentHost.RegisterTimer(
            TaskTickTimerName,
            SurfaceSquadMessageTypes.TaskTick,
            "continue",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero);
    }

    private void Stamp(AgentMessage message)
    {
        message.MessageType ??= SurfaceSquadMessageTypes.Chat;
        message.Args ??= new Dictionary<string, string>();
        message.Args[SurfaceSquadArgs.SquadHandle] = runtime.Squad.OrchestratorHandle;
        message.Args[SurfaceSquadArgs.SquadName] = runtime.Squad.Name;
        message.Args[SurfaceSquadArgs.SquadSlug] = runtime.Squad.Slug;
        message.Channel ??= runtime.Squad.OrchestratorHandle;
    }

    private async Task PersistAsync()
    {
        taskLogger.LogDebug(
            "Surface Task persisting state - Handle: {Handle}, IsRunning: {IsRunning}, IsBlocked: {IsBlocked}, TaskCount: {TaskCount}, Pending: {PendingCount}, Completed: {CompletedCount}, Failed: {FailedCount}",
            fabrcoreAgentHost.GetHandle(),
            state.IsRunning,
            state.IsBlocked,
            state.Tasks.Count,
            CountTasks(SurfaceTaskItemStatus.Pending),
            CountTasks(SurfaceTaskItemStatus.Completed),
            CountTasks(SurfaceTaskItemStatus.Failed));
        SetState(StateKey, state);
        await FlushStateAsync();
    }

    private async Task<SurfaceTaskRunState> LoadStateAsync()
    {
        var stateRead = await TryGetStateAsync<SurfaceTaskRunState>(StateKey);
        if (stateRead.Succeeded)
        {
            return stateRead.Value ?? new SurfaceTaskRunState();
        }

        taskLogger.LogWarning(
            stateRead.Error,
            "Surface Task state could not be loaded and will be reset - Handle: {Handle}, StateKey: {StateKey}, ValueKind: {ValueKind}",
            fabrcoreAgentHost.GetHandle(),
            stateRead.Key,
            stateRead.ValueKind);

        RemoveState(stateRead.Key);
        await FlushStateAsync();
        return new SurfaceTaskRunState();
    }

    protected override Dictionary<string, string>? GetCustomHealthMetrics(HealthDetailLevel detailLevel)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TaskSquadName"] = runtime.Squad.Name,
            ["TaskSquadHandle"] = runtime.Squad.OrchestratorHandle,
            ["TaskSquadType"] = runtime.Squad.SquadType.ToString(),
            ["TaskAgentCount"] = runtime.Squad.Agents.Count.ToString(),
            ["TaskExecutorCount"] = CountRole(SurfaceSquadMemberRole.Executor).ToString(),
            ["TaskSmeCount"] = CountRole(SurfaceSquadMemberRole.SubjectMatterExpert).ToString(),
            ["TaskPlannerClientReady"] = (plannerClient is not null).ToString(),
            ["TaskWorkerClientReady"] = (workerClient is not null).ToString(),
            ["TaskBusReady"] = (bus is not null).ToString(),
            ["TaskIsRunning"] = state.IsRunning.ToString(),
            ["TaskIsBlocked"] = state.IsBlocked.ToString(),
            ["TaskCount"] = state.Tasks.Count.ToString(),
            ["TaskPendingCount"] = CountTasks(SurfaceTaskItemStatus.Pending).ToString(),
            ["TaskInProgressCount"] = CountTasks(SurfaceTaskItemStatus.InProgress).ToString(),
            ["TaskCompletedCount"] = CountTasks(SurfaceTaskItemStatus.Completed).ToString(),
            ["TaskFailedCount"] = CountTasks(SurfaceTaskItemStatus.Failed).ToString(),
            ["TaskValidationAttempts"] = state.ValidationAttempts.ToString(),
            ["TaskHasPersistedRuntimeArg"] = config.Args.ContainsKey(SurfaceSquadArgs.SquadDefinition).ToString()
        };

    private int CountRole(SurfaceSquadMemberRole role)
        => runtime.Squad.Agents.Count(agent => agent.Role == role);

    private int CountTasks(SurfaceTaskItemStatus status)
        => state.Tasks.Count(task => task.Status == status);

    private string FormatAgentRoles()
        => string.Join(", ", runtime.Squad.Agents.Select(agent => $"{agent.Name}:{agent.Role}:{agent.Handle}"));

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}

public sealed class SurfaceTaskRunState
{
    public string Goal { get; set; } = string.Empty;

    public string CallerHandle { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsBlocked { get; set; }

    public int ValidationAttempts { get; set; }

    public string? FinalResult { get; set; }

    public List<SurfaceTaskItem> Tasks { get; set; } = [];
}

public sealed class SurfaceTaskItem
{
    public int Order { get; set; }

    public string Description { get; set; } = string.Empty;

    public string AssignedAgent { get; set; } = string.Empty;

    public string AssignedAgentName { get; set; } = string.Empty;

    public SurfaceTaskItemStatus Status { get; set; } = SurfaceTaskItemStatus.Pending;

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; } = 2;

    public string? Result { get; set; }

    public string? FailureReason { get; set; }

    public string? RoadblockNote { get; set; }

    public List<string> Warnings { get; set; } = [];
}

public enum SurfaceTaskItemStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3
}

internal sealed class SurfaceTaskPlanDraft
{
    public List<SurfaceTaskPlanDraftItem> Tasks { get; set; } = [];

    public static SurfaceTaskPlanDraft? Parse(string text)
    {
        var json = JsonPayload.Extract(text);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SurfaceTaskPlanDraft>(json, SurfaceJson.Options);
    }
}

internal sealed class SurfaceTaskPlanDraftItem
{
    public string Description { get; set; } = string.Empty;

    public string? AgentName { get; set; }
}

internal sealed class SurfaceTaskValidationResult
{
    public bool IsSatisfied { get; set; }

    public string Summary { get; set; } = string.Empty;

    public List<string> Missing { get; set; } = [];

    public static SurfaceTaskValidationResult? Parse(string text)
    {
        var json = JsonPayload.Extract(text);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SurfaceTaskValidationResult>(json, SurfaceJson.Options);
    }
}

internal sealed record SurfaceTaskDelegationResult(
    bool Success,
    string ProseText,
    List<string> Warnings,
    string Summary,
    string? FailureReason)
{
    public static SurfaceTaskDelegationResult FromMessage(string? raw)
    {
        var text = raw ?? string.Empty;
        var envelope = SurfaceTaskEnvelope.TryExtract(text);
        var prose = SurfaceTaskEnvelope.Strip(text).Trim();
        if (envelope is null)
        {
            return new SurfaceTaskDelegationResult(true, prose, [], FirstLine(prose), null);
        }

        var success = !string.Equals(envelope.Status, "failed", StringComparison.OrdinalIgnoreCase);
        return new SurfaceTaskDelegationResult(
            success,
            prose,
            envelope.Warnings,
            string.IsNullOrWhiteSpace(envelope.Summary) ? FirstLine(prose) : envelope.Summary,
            success ? null : envelope.Summary);
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "No summary." : line.Trim();
    }
}

internal sealed class SurfaceTaskEnvelope
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    public static SurfaceTaskEnvelope? TryExtract(string text)
    {
        var json = JsonPayload.ExtractFenced(text, "fabrcore-envelope");
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SurfaceTaskEnvelope>(json, SurfaceJson.Options);
    }

    public static string Strip(string text)
        => JsonPayload.RemoveFenced(text, "fabrcore-envelope");
}

internal static class JsonPayload
{
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

    public static string? ExtractFenced(string text, string fenceName)
    {
        var marker = "```" + fenceName;
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var contentStart = text.IndexOf('\n', start);
        if (contentStart < 0)
        {
            return null;
        }

        var end = text.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
        return end < 0 ? null : text[(contentStart + 1)..end].Trim();
    }

    public static string RemoveFenced(string text, string fenceName)
    {
        var marker = "```" + fenceName;
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf("```", start + marker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return text[..start];
        }

        return (text[..start] + text[(end + 3)..]).Trim();
    }
}
