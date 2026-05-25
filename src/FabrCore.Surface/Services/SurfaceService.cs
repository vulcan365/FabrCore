using System.Collections.Concurrent;
using System.Text;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Brain;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Validation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Services;

internal sealed class SurfaceService : ISurfaceService
{
    private readonly IFabrCoreAgentHost agentHost;
    private readonly AIAgent? defaultPlanningAgent;
    private readonly SurfaceAgentFactory? agentFactory;
    private readonly SurfaceAiOptions aiOptions;
    private readonly SurfaceOptions surfaceOptions;
    private readonly ILogger<SurfaceService> logger;
    private readonly ConcurrentDictionary<string, AIAgent> agentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim agentCreateLock = new(1, 1);

    public SurfaceService(
        string agentHandle,
        IFabrCoreAgentHost agentHost,
        AIAgent? defaultPlanningAgent,
        SurfaceAgentFactory? agentFactory,
        SurfaceDefinition definition,
        SurfaceAiOptions aiOptions,
        SurfaceOptions surfaceOptions,
        ILogger<SurfaceService> logger)
    {
        AgentHandle = agentHandle;
        this.agentHost = agentHost;
        this.defaultPlanningAgent = defaultPlanningAgent;
        this.agentFactory = agentFactory;
        Definition = definition;
        this.aiOptions = aiOptions;
        this.surfaceOptions = surfaceOptions;
        this.logger = logger;
    }

    public string AgentHandle { get; }

    public SurfaceDefinition Definition { get; }

    public async Task<AdaptiveCardSurfaceEnvelope> PlanAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var planner = await ResolvePlanningAgentAsync(cancellationToken);
        var boundedPrompt = prompt.Length > aiOptions.MaxPromptCharacters
            ? prompt[..aiOptions.MaxPromptCharacters]
            : prompt;

        var session = await planner.CreateSessionAsync();
        var result = await planner.RunAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                new ChatMessage(ChatRole.User, boundedPrompt)
            },
            session,
            cancellationToken: cancellationToken);

        var text = result.Messages.LastOrDefault()?.Text ?? string.Empty;
        var envelope = SurfaceEnvelope.TryExtractEnvelope(text)
                       ?? throw new InvalidOperationException("Surface planner did not return a valid fabrcore-adaptive-card-surface block.");

        ValidateOrThrow(envelope);
        return envelope;
    }

    public async Task<AgentMessage> RenderAsync(
        AdaptiveCardSurfaceEnvelope envelope,
        AgentMessage sourceMessage,
        string? targetHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(sourceMessage);

        var validation = ValidateOrThrow(envelope);
        var resolvedTargetHandle = ResolveTargetHandle(targetHandle, sourceMessage, envelope);
        var message = SurfaceMessageFactory.CreateRenderMessage(envelope, sourceMessage, resolvedTargetHandle);
        ApplyDiagnostics(message, resolvedTargetHandle, validation);

        if (aiOptions.SendRenderMessages)
        {
            await agentHost.SendMessage(message);
        }

        return message;
    }

    public async Task<AgentMessage> PlanAndRenderAsync(
        string prompt,
        AgentMessage sourceMessage,
        string? targetHandle = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await PlanAsync(prompt, cancellationToken);
        return await RenderAsync(envelope, sourceMessage, targetHandle, cancellationToken);
    }

    private async Task<AIAgent> ResolvePlanningAgentAsync(CancellationToken cancellationToken)
    {
        var modelName = Definition.PlanningModelName ?? aiOptions.DefaultPlanningModelName;
        if (!string.IsNullOrWhiteSpace(modelName) && agentFactory is not null)
        {
            if (agentCache.TryGetValue(modelName, out var cached))
            {
                return cached;
            }

            await agentCreateLock.WaitAsync(cancellationToken);
            try
            {
                if (agentCache.TryGetValue(modelName, out cached))
                {
                    return cached;
                }

                var agent = await agentFactory(modelName, cancellationToken);
                agentCache[modelName] = agent;
                return agent;
            }
            finally
            {
                agentCreateLock.Release();
            }
        }

        if (defaultPlanningAgent is not null)
        {
            return defaultPlanningAgent;
        }

        throw new InvalidOperationException(
            "Surface prompt planning requires a planning AIAgent or a SurfaceAgentFactory. Use RenderAsync with an explicit AdaptiveCardSurfaceEnvelope for deterministic JSON rendering.");
    }

    private AdaptiveCardSurfaceValidationResult ValidateOrThrow(AdaptiveCardSurfaceEnvelope envelope)
    {
        var result = new AdaptiveCardSurfaceValidator(surfaceOptions, Definition).Validate(envelope);
        if (result.IsValid)
        {
            return result;
        }

        var message = "Surface envelope failed validation: " + string.Join("; ", result.Errors);
        logger.LogWarning("{ValidationMessage}", message);
        throw new InvalidOperationException(message);
    }

    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You convert user requests into trusted Adaptive Card Surface envelopes.");
        sb.AppendLine("Return a short acknowledgement, then exactly one fenced block named fabrcore-adaptive-card-surface containing valid JSON.");
        sb.AppendLine("Do not emit Razor, HTML, SQL, JavaScript, arbitrary API routes, or component names.");
        sb.AppendLine("The envelope has version, id, card, optional data, and optional metadata.");
        sb.AppendLine("The card property must be an Adaptive Card template JSON object with type 'AdaptiveCard'.");
        sb.AppendLine("Use Adaptive Card template bindings such as ${name} when data is supplied.");
        sb.AppendLine("Maximum Adaptive Card schema version: " + Definition.MaxAdaptiveCardVersion + ".");
        sb.AppendLine("Allowed Adaptive Card action types: " + string.Join(", ", Definition.AllowedActionTypes));

        if (!Definition.AllowAnyActionVerb.GetValueOrDefault(true) && Definition.AllowedActionVerbs.Count > 0)
        {
            sb.AppendLine("Allowed Action.Execute verbs: " + string.Join(", ", Definition.AllowedActionVerbs));
        }

        if (Definition.AllowedTargetAgents.Count > 0)
        {
            sb.AppendLine("Allowed target agent overrides: " + string.Join(", ", Definition.AllowedTargetAgents));
        }

        if (Definition.RequiredActions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Required Adaptive Card actions:");
            foreach (var action in Definition.RequiredActions)
            {
                sb.Append("- For ");
                sb.Append(string.IsNullOrWhiteSpace(action.AppliesTo) ? "matching records" : action.AppliesTo);
                sb.Append(": include an ");
                sb.Append(AdaptiveCardActionTypes.Execute);
                sb.Append(" action titled '");
                sb.Append(action.Title);
                sb.Append("' with verb '");
                sb.Append(action.Verb);
                sb.Append("'. Put data.actionId='");
                sb.Append(action.Verb);
                sb.Append("', data.routeTo='");
                sb.Append(action.RouteTo);
                sb.Append("'");
                if (!string.IsNullOrWhiteSpace(action.TargetAgent))
                {
                    sb.Append(", data.targetAgent='");
                    sb.Append(action.TargetAgent);
                    sb.Append("'");
                }

                sb.Append(", and data.");
                sb.Append(action.IdField);
                sb.Append(" bound from the record id field. ");
                if (!string.IsNullOrWhiteSpace(action.MessageTemplate))
                {
                    sb.Append("Use data.messageTemplate='");
                    sb.Append(action.MessageTemplate);
                    sb.Append("'.");
                }

                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(Definition.SystemPrompt))
        {
            sb.AppendLine();
            sb.AppendLine(Definition.SystemPrompt);
        }

        sb.AppendLine();
        sb.AppendLine("Envelope shape:");
        sb.AppendLine("```fabrcore-adaptive-card-surface");
        sb.AppendLine("{");
        sb.AppendLine("  \"version\": \"2.0\",");
        sb.AppendLine("  \"id\": \"stable-id\",");
        sb.AppendLine("  \"card\": { \"type\": \"AdaptiveCard\", \"version\": \"1.6\", \"body\": [] },");
        sb.AppendLine("  \"data\": { }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string? ResolveTargetHandle(
        string? explicitTargetHandle,
        AgentMessage sourceMessage,
        AdaptiveCardSurfaceEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(explicitTargetHandle))
        {
            return explicitTargetHandle;
        }

        if (TryGetArg(sourceMessage, SurfaceMessageArgs.TargetHandle, out var targetHandle)
            || TryGetArg(sourceMessage, SurfaceMessageArgs.SurfaceTargetHandle, out targetHandle))
        {
            return targetHandle;
        }

        return envelope.Metadata?.TargetHandle;
    }

    private void ApplyDiagnostics(
        AgentMessage message,
        string? targetHandle,
        AdaptiveCardSurfaceValidationResult validation)
    {
        if (!surfaceOptions.EnableDiagnostics)
        {
            return;
        }

        message.Args ??= new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(targetHandle))
        {
            message.Args[SurfaceDiagnosticArgs.TargetHandle] = targetHandle;
        }

        message.Args[SurfaceDiagnosticArgs.PlannedActionCount] = validation.PlannedActionCount.ToString();
        message.Args[SurfaceDiagnosticArgs.ValidatedActionCount] = validation.ValidatedActionCount.ToString();
        message.Args[SurfaceDiagnosticArgs.RejectedActionCount] = validation.RejectedActionCount.ToString();

        logger.LogInformation(
            "Surface render target={TargetHandle}; plannedActions={PlannedActionCount}; validatedActions={ValidatedActionCount}; rejectedActions={RejectedActionCount}",
            targetHandle,
            validation.PlannedActionCount,
            validation.ValidatedActionCount,
            validation.RejectedActionCount);
    }

    private static bool TryGetArg(AgentMessage message, string key, out string value)
    {
        if (message.Args is not null
            && message.Args.TryGetValue(key, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
