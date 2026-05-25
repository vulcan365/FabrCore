using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Surface.Agents;

[AgentAlias("surface")]
[Description("Prebuilt FabrCore Surface agent that converts prompts into trusted Adaptive Card render messages.")]
[FabrCoreCapabilities("Accepts a user prompt, plans an AdaptiveCardSurfaceEnvelope using fabrcore-surface.json, and sends ui.render messages to a Blazor Surface client.")]
public sealed class SurfaceAgent : FabrCoreAgentProxy
{
    private ISurfaceProvider? surfaceProvider;
    private ISurfaceService? surfaceService;
    private AIAgent? planningAgent;

    public SurfaceAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        surfaceProvider = serviceProvider.GetRequiredService<ISurfaceProvider>();
        var options = serviceProvider.GetService<SurfaceAiOptions>() ?? new SurfaceAiOptions();
        var configName = ResolveConfigName(options);
        var modelName = config.Models ?? options.DefaultPlanningModelName ?? "default";

        var agentResult = await CreateChatClientAgent(
            modelName,
            $"{fabrcoreAgentHost.GetHandle()}:surface-planner");
        planningAgent = agentResult.Agent;

        surfaceService = await surfaceProvider.GetSurfaceServiceAsync(
            fabrcoreAgentHost,
            fabrcoreAgentHost.GetHandle(),
            configName,
            planningAgent,
            CreatePlanningAgentAsync);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        if (surfaceService is null)
        {
            throw new InvalidOperationException("Surface agent has not been initialized.");
        }

        var prompt = message.Message;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var empty = message.Response();
            empty.Message = "Send me a description of the UI you want rendered.";
            return empty;
        }

        await surfaceService.PlanAndRenderAsync(prompt, message);

        var response = message.Response();
        response.Message = "Rendered an Adaptive Card Surface envelope.";
        return response;
    }

    private async Task<AIAgent> CreatePlanningAgentAsync(string modelName, CancellationToken cancellationToken)
    {
        var result = await CreateChatClientAgent(
            modelName,
            $"{fabrcoreAgentHost.GetHandle()}:surface-planner:{modelName}");
        return result.Agent;
    }

    private string ResolveConfigName(SurfaceAiOptions options)
    {
        if (config.Args.TryGetValue(SurfaceMessageArgs.SurfaceConfig, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return options.DefaultSurfaceDefinitionName;
    }
}
