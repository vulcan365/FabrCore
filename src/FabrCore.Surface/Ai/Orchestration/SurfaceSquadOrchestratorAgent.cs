using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Squads;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Surface.Ai.Orchestration;

[AgentAlias(SurfaceOrchestrationAgentTypes.SquadOrchestrator)]
[Description("Built-in router for Surface orchestrator squads.")]
[FabrCoreCapabilities("Routes user messages to the best agent in a Surface squad by using live FabrCore registry metadata, agent health, plugins, tools, and agent notes, then formats the delegated response for the user.")]
public sealed class SurfaceSquadOrchestratorAgent : FabrCoreAgentProxy
{
    private SurfaceSquadRuntime runtime = new();
    private SurfaceSquadConversationBus? bus;
    private IFabrCoreRegistry? registry;
    private IChatClient? routeClient;
    private IChatClient? responseClient;

    public SurfaceSquadOrchestratorAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        runtime = SurfaceSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        bus = new SurfaceSquadConversationBus(fabrcoreAgentHost, runtime);
        registry = serviceProvider.GetService<IFabrCoreRegistry>();

        var modelName = config.Models ?? "default";
        routeClient = await GetChatClient(modelName);
        responseClient = await GetChatClient(modelName);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        Stamp(response);

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            response.Message = "Send a request for this orchestrator squad.";
            return response;
        }

        if (routeClient is null || responseClient is null || bus is null)
        {
            response.Message = "Squad orchestrator is not initialized.";
            return response;
        }

        if (runtime.Squad.Agents.Count == 0)
        {
            response.Message = "Add agents to this orchestrator squad before sending work to it.";
            return response;
        }

        var capabilities = await BuildCapabilitiesAsync();
        var available = capabilities
            .Where(capability => capability.IsConfigured)
            .ToList();

        if (available.Count == 0)
        {
            response.Message = "No configured agents are currently available in this orchestrator squad.";
            return response;
        }

        var decision = await ChooseRouteAsync(message.Message!, capabilities, excludedHandles: []);
        var target = ResolveTarget(decision, available);
        if (target is null)
        {
            response.Message = "I could not identify a suitable configured agent for this request.";
            return response;
        }

        var agentReply = await AskAgentAsync(target, decision.Message, message);
        if (string.Equals(agentReply.MessageType, SystemMessageTypes.Error, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(agentReply.Message))
        {
            var retryDecision = await ChooseRouteAsync(
                message.Message!,
                capabilities,
                excludedHandles: [target.Handle]);
            var retryTarget = ResolveTarget(retryDecision, available.Where(a =>
                !string.Equals(a.Handle, target.Handle, StringComparison.OrdinalIgnoreCase)));

            if (retryTarget is not null)
            {
                target = retryTarget;
                decision = retryDecision;
                agentReply = await AskAgentAsync(target, decision.Message, message);
            }
        }

        response.Message = await FormatResponseAsync(message.Message!, target, decision.Message, agentReply.Message ?? string.Empty);
        return response;
    }

    private Task<List<SurfaceSquadAgentCapability>> BuildCapabilitiesAsync()
        => SurfaceSquadCapabilityLoader.BuildAsync(runtime.Squad, fabrcoreAgentHost, registry, includeRoleNote: false, logger);

    private async Task<SurfaceSquadRouteDecision> ChooseRouteAsync(
        string userMessage,
        IReadOnlyList<SurfaceSquadAgentCapability> capabilities,
        IReadOnlyCollection<string> excludedHandles)
    {
        var prompt = $$"""
            You route messages inside the Surface orchestrator squad "{{runtime.Squad.Name}}".

            Choose exactly one configured agent and write the exact message to send it.
            Return only JSON with this shape:
            {"agentName":"agent name or handle","message":"message to send","reason":"short reason"}

            User message:
            {{userMessage}}

            Excluded handles:
            {{FormatExcluded(excludedHandles)}}

            Squad agents:
            {{FormatCapabilities(capabilities)}}
            """;

        var result = await routeClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var text = result.Text ?? string.Empty;
        return SurfaceSquadRouteDecision.Parse(text) ?? new SurfaceSquadRouteDecision
        {
            AgentName = capabilities.FirstOrDefault(c =>
                c.IsConfigured && !excludedHandles.Contains(c.Handle, StringComparer.OrdinalIgnoreCase))?.Name ?? string.Empty,
            Message = userMessage,
            Reason = "Fallback route"
        };
    }

    private async Task<AgentMessage> AskAgentAsync(
        SurfaceSquadAgentCapability target,
        string? routedMessage,
        AgentMessage source)
    {
        try
        {
            return await bus!.SendAndReceiveAsync(new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = target.Handle,
                MessageType = SurfaceSquadMessageTypes.AgentRequest,
                Message = string.IsNullOrWhiteSpace(routedMessage) ? source.Message : routedMessage,
                Kind = MessageKind.Request,
                Args = new Dictionary<string, string>
                {
                    [SurfaceSquadArgs.AgentName] = target.Name
                }
            });
        }
        catch (Exception ex)
        {
            return new AgentMessage
            {
                FromHandle = target.Handle,
                ToHandle = fabrcoreAgentHost.GetHandle(),
                MessageType = SystemMessageTypes.Error,
                Kind = MessageKind.Response,
                Message = $"Agent '{target.Name}' could not be reached: {ex.Message}"
            };
        }
    }

    private async Task<string> FormatResponseAsync(
        string userMessage,
        SurfaceSquadAgentCapability target,
        string delegatedMessage,
        string agentResponse)
    {
        if (string.IsNullOrWhiteSpace(agentResponse))
        {
            return $"I routed this to {target.Name}, but it did not return a usable response.";
        }

        var prompt = $"""
            Format the delegated agent response for the user.
            Keep factual content from the agent response. Do not invent details.
            Use concise Markdown when structure helps.

            Original user message:
            {userMessage}

            Routed agent:
            {target.Name} ({target.AgentType})

            Delegated message:
            {delegatedMessage}

            Agent response:
            {agentResponse}
            """;

        var result = await responseClient!.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
        var formatted = result.Text;
        return string.IsNullOrWhiteSpace(formatted) ? agentResponse : formatted!;
    }

    private static SurfaceSquadAgentCapability? ResolveTarget(
        SurfaceSquadRouteDecision decision,
        IEnumerable<SurfaceSquadAgentCapability> available)
    {
        if (string.IsNullOrWhiteSpace(decision.AgentName))
        {
            return available.FirstOrDefault();
        }

        return available.FirstOrDefault(candidate =>
                   string.Equals(candidate.Name, decision.AgentName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(candidate.Handle, decision.AgentName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(ShortHandle(candidate.Handle), decision.AgentName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(candidate.AgentType, decision.AgentName, StringComparison.OrdinalIgnoreCase))
               ?? available.FirstOrDefault();
    }

    private static string FormatCapabilities(IEnumerable<SurfaceSquadAgentCapability> capabilities)
        => string.Join(Environment.NewLine + Environment.NewLine, capabilities.Select(capability =>
        {
            var status = capability.IsConfigured
                ? "configured"
                : $"unavailable: {capability.UnavailableReason ?? "not configured"}";
            return $"""
                - name: {capability.Name}
                  handle: {capability.Handle}
                  type: {capability.AgentType}
                  status: {status}
                  description: {capability.Description}
                  plugins: {string.Join(", ", capability.Plugins)}
                  tools: {string.Join(", ", capability.Tools)}
                  notes: {capability.Notes}
                """;
        }));

    private static string FormatExcluded(IEnumerable<string> excludedHandles)
    {
        var list = excludedHandles.Where(handle => !string.IsNullOrWhiteSpace(handle)).ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
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

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }

    private sealed class SurfaceSquadRouteDecision
    {
        public string AgentName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string? Reason { get; set; }

        public static SurfaceSquadRouteDecision? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var json = ExtractJson(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<SurfaceSquadRouteDecision>(json, SurfaceJson.Options);
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJson(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineEnd = trimmed.IndexOf('\n');
                if (firstLineEnd >= 0)
                {
                    trimmed = trimmed[(firstLineEnd + 1)..];
                }

                var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0)
                {
                    trimmed = trimmed[..fence];
                }
            }

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
        }
    }
}
