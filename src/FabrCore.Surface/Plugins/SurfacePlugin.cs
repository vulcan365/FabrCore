using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Brain;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Plugins;

[PluginAlias("surface-ui")]
[Description("Renders trusted Adaptive Card Surface envelopes for the current user.")]
[FabrCoreCapabilities("Render Adaptive Card template JSON and data by sending ui.render AgentMessage payloads to a FabrCore Surface client.")]
public sealed class SurfacePlugin : IFabrCorePlugin
{
    private AgentConfiguration? config;
    private IFabrCoreAgentHost? agentHost;
    private ISurfaceProvider? surfaceProvider;
    private IFabrCoreChatClientService? chatClientService;
    private ILoggerFactory? loggerFactory;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        this.config = config;
        agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        surfaceProvider = serviceProvider.GetRequiredService<ISurfaceProvider>();
        chatClientService = serviceProvider.GetService<IFabrCoreChatClientService>();
        loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        return Task.CompletedTask;
    }

    [Description("Render an Adaptive Card Surface envelope in one call. Pass either envelope JSON or a natural-language prompt. JSON is deterministic; prompt planning requires Surface planning model configuration.")]
    [FabrCoreCapabilities("Sends a ui.render message to the user's Surface UI. Use adaptiveCardEnvelopeJsonOrPrompt for either raw AdaptiveCardSurfaceEnvelope JSON or a prompt such as 'show an approval card'. Optional targetHandle overrides the message recipient.")]
    public async Task<string> RenderSurfaceAsync(
        [Description("A serialized AdaptiveCardSurfaceEnvelope JSON object, a fabrcore-adaptive-card-surface fenced block, or a prompt to convert into a Surface envelope.")]
        string adaptiveCardEnvelopeJsonOrPrompt,
        [Description("Optional handle to receive the UI render message. Leave empty to reply to the sender of the active agent message.")]
        string? targetHandle = null,
        [Description("Optional name of a surface definition from fabrcore-surface.json.")]
        string? surfaceConfigName = null)
    {
        if (agentHost is null || surfaceProvider is null || config is null)
        {
            throw new InvalidOperationException("Surface plugin has not been initialized.");
        }

        var recipient = string.IsNullOrWhiteSpace(targetHandle)
            ? agentHost.GetOwnerHandle()
            : targetHandle;

        var source = new AgentMessage
        {
            FromHandle = recipient,
            ToHandle = agentHost.GetHandle(),
            Kind = MessageKind.Request
        };

        var service = await surfaceProvider.GetSurfaceServiceAsync(
            agentHost,
            agentHost.GetHandle(),
            ResolveConfigName(surfaceConfigName),
            agentFactory: CreatePlanningAgentAsync);

        AdaptiveCardSurfaceEnvelope? envelope = TryParseEnvelope(adaptiveCardEnvelopeJsonOrPrompt);
        AgentMessage renderMessage;
        if (envelope is not null)
        {
            renderMessage = await service.RenderAsync(envelope, source, targetHandle);
        }
        else
        {
            renderMessage = await service.PlanAndRenderAsync(adaptiveCardEnvelopeJsonOrPrompt, source, targetHandle);
        }

        return $"Surface render sent. messageType={renderMessage.MessageType}; dataType={renderMessage.DataType}; to={renderMessage.ToHandle}";
    }

    private string? ResolveConfigName(string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        if (config?.Args.TryGetValue(SurfaceMessageArgs.SurfaceConfig, out var configured) == true)
        {
            return configured;
        }

        return null;
    }

    private async Task<AIAgent> CreatePlanningAgentAsync(string modelName, CancellationToken cancellationToken)
    {
        if (chatClientService is null || agentHost is null)
        {
            throw new InvalidOperationException(
                "Surface prompt planning from the plugin requires IFabrCoreChatClientService. Pass AdaptiveCardSurfaceEnvelope JSON for deterministic rendering when no chat client service is registered.");
        }

        var resolvedModel = string.IsNullOrWhiteSpace(modelName)
            ? config?.Models ?? "default"
            : modelName;
        var chatClient = await chatClientService.GetChatClient(resolvedModel);
        var logger = loggerFactory?.CreateLogger<SurfacePlugin>();
        var historyProvider = FabrCoreChatHistoryProvider.Create(
            agentHost,
            $"{agentHost.GetHandle()}:surface-plugin:{resolvedModel}",
            logger);

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = agentHost.GetHandle(),
                ChatHistoryProvider = historyProvider,
                ChatOptions = new ChatOptions
                {
                    Instructions = config?.SystemPrompt
                }
            });
    }

    private static AdaptiveCardSurfaceEnvelope? TryParseEnvelope(string text)
    {
        var fenced = SurfaceEnvelope.TryExtractEnvelope(text);
        if (fenced is not null)
        {
            return fenced;
        }

        try
        {
            return JsonSerializer.Deserialize<AdaptiveCardSurfaceEnvelope>(text, SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
