#pragma warning disable MAAI001 // Harness providers are for evaluation purposes only and may change.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Squads;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Surface.Ai.Tasks;

/// <summary>
/// Adapts a squad member into an <see cref="AIAgent"/> so it can be delegated to by
/// <see cref="BackgroundAgentsProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>A2AAgentProxy</c>: that proxy leaves <see cref="Name"/> and <see cref="Description"/>
/// null (the provider rejects empty names), bypasses <see cref="SurfaceSquadConversationBus"/> so nothing is
/// mirrored into the principal's timeline, and has no delegation timeout.
/// </para>
/// <para>
/// The delegate's reply is returned verbatim. There is no envelope, status token, or summary extraction —
/// the coordinating model reads the prose and decides what it means.
/// </para>
/// </remarks>
internal sealed class SurfaceSquadMemberAgent : AIAgent
{
    private readonly SurfaceSquadConversationBus bus;
    private readonly IFabrCoreAgentHost agentHost;
    private readonly SurfaceSquad squad;
    private readonly SurfaceSquadAgent member;
    private readonly string? clientAgentOverlay;
    private readonly TimeSpan timeout;

    internal SurfaceSquadMemberAgent(
        SurfaceSquadConversationBus bus,
        IFabrCoreAgentHost agentHost,
        SurfaceSquad squad,
        SurfaceSquadAgent member,
        string name,
        string description,
        string? clientAgentOverlay,
        TimeSpan timeout)
    {
        this.bus = bus;
        this.agentHost = agentHost;
        this.squad = squad;
        this.member = member;
        this.clientAgentOverlay = clientAgentOverlay;
        this.timeout = timeout;
        Name = name;
        Description = description;
    }

    public override string? Name { get; }

    public override string? Description { get; }

    /// <summary>The squad member this agent delegates to.</summary>
    internal SurfaceSquadAgent Member => member;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new SurfaceSquadMemberSession());

    // Delegation is stateless: each call carries its full instruction and the member agent owns its own
    // durable history in its grain. Nothing needs to round-trip through the coordinator's session.
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session.StateBag, jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new SurfaceSquadMemberSession());

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var instruction = string.Join(Environment.NewLine, messages.Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

        agentHost.SetStatusMessage($"Delegating to {Name}...");

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(clientAgentOverlay))
        {
            body.AppendLine(clientAgentOverlay!.Trim());
            body.AppendLine();
        }

        body.Append(instruction);

        var request = new AgentMessage
        {
            FromHandle = agentHost.GetHandle(),
            ToHandle = member.Handle,
            MessageType = SurfaceSquadMessageTypes.TaskDelegation,
            Kind = MessageKind.Request,
            Message = body.ToString(),
            State = new Dictionary<string, string>
            {
                [SurfaceSquadArgs.SquadHandle] = squad.OrchestratorHandle,
                [SurfaceSquadArgs.SquadName] = squad.Name,
                [SurfaceSquadArgs.AgentName] = member.Name,
                [SurfaceSquadArgs.AgentRole] = member.Role.ToString()
            }
        };

        // The host send has no cancellation surface, so bound it here. A breach abandons the wait and
        // surfaces as a failed background task; BackgroundAgentsProvider maps the exception to
        // BackgroundTaskStatus.Failed and the model sees the reason via background_agents_get_task_results.
        var response = await bus.SendAndReceiveAsync(request).WaitAsync(timeout, cancellationToken);

        var text = response.Message ?? string.Empty;
        return new AgentResponse([new ChatMessage(ChatRole.Assistant, text) { AuthorName = Name }]);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new AgentResponseUpdate(message.Role, message.Contents) { AuthorName = message.AuthorName };
        }
    }

    /// <summary>
    /// Projects squad members into delegation agents, resolving the unique non-empty names that
    /// <see cref="BackgroundAgentsProvider"/> requires and stating each member's role in its description.
    /// Members that are unavailable (failed health probe, unconfigured) are excluded.
    /// </summary>
    internal static List<SurfaceSquadMemberAgent> BuildAgents(
        SurfaceSquad squad,
        IReadOnlyList<SurfaceSquadAgentCapability> capabilities,
        SurfaceSquadConversationBus bus,
        IFabrCoreAgentHost agentHost,
        string? clientAgentOverlay,
        TimeSpan timeout,
        out List<string> excluded)
    {
        excluded = [];
        var agents = new List<SurfaceSquadMemberAgent>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in squad.Agents)
        {
            if (member.Role == SurfaceSquadMemberRole.Helper)
            {
                continue;
            }

            var capability = capabilities.FirstOrDefault(c =>
                string.Equals(c.Handle, member.Handle, StringComparison.OrdinalIgnoreCase));

            if (capability is not null && !string.IsNullOrWhiteSpace(capability.UnavailableReason))
            {
                excluded.Add($"{member.Name} ({capability.UnavailableReason})");
                continue;
            }

            var name = ResolveName(member, used);
            var description = BuildDescription(member, capability);

            agents.Add(new SurfaceSquadMemberAgent(
                bus,
                agentHost,
                squad,
                member,
                name,
                description,
                clientAgentOverlay,
                timeout));
        }

        return agents;
    }

    private static string ResolveName(SurfaceSquadAgent member, HashSet<string> used)
    {
        var candidate = !string.IsNullOrWhiteSpace(member.Name)
            ? member.Name.Trim()
            : SurfaceSquadCapabilityLoader.ShortHandle(member.Handle);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "agent";
        }

        var name = candidate;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = $"{candidate}-{suffix++}";
        }

        return name;
    }

    private static string BuildDescription(SurfaceSquadAgent member, SurfaceSquadAgentCapability? capability)
    {
        var description = capability?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = !string.IsNullOrWhiteSpace(member.Description)
                ? member.Description!
                : $"Agent {member.Name}";
        }

        var role = member.Role == SurfaceSquadMemberRole.SubjectMatterExpert
            ? "Role: Subject matter expert - advisory only. Consult for guidance; do not assign execution work."
            : "Role: Executor - assign work to this agent.";

        return $"{description}{Environment.NewLine}{role}";
    }
}

internal sealed class SurfaceSquadMemberSession : AgentSession
{
    internal SurfaceSquadMemberSession()
    {
    }
}
