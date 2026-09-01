#pragma warning disable MAAI001 // Harness providers (BackgroundAgentsProvider) are for evaluation purposes only and may change.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>
/// Adapts a FabrCore agent handle into an <see cref="AIAgent"/> so a harness can delegate to it through the
/// <c>background_agents_*</c> tools.
/// </summary>
/// <remarks>
/// <para>
/// <c>BackgroundAgentsProvider</c> rejects unnamed agents outright, so <see cref="Name"/> and
/// <see cref="Description"/> are always populated here. Delegation is also bounded by a timeout, so a
/// wedged target cannot hang the delegating agent's turn indefinitely.
/// </para>
/// <para>
/// Delegation is stateless. Each call carries its full instruction, and the target agent owns its own durable
/// history in its own grain — nothing needs to round-trip through the delegating agent's session.
/// </para>
/// <para>
/// The reply is returned verbatim. There is no envelope, status token, or summary extraction: the delegating
/// model reads the prose and decides what it means.
/// </para>
/// </remarks>
public sealed class FabrCoreBackgroundAgent : AIAgent
{
    /// <summary>Default bound on a single delegation.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IFabrCoreAgentHost agentHost;
    private readonly string handle;
    private readonly string? instructionOverlay;
    private readonly string? messageType;
    private readonly TimeSpan timeout;

    /// <summary>
    /// Creates a delegation agent targeting a FabrCore agent handle.
    /// </summary>
    /// <param name="agentHost">Host used to send the delegation.</param>
    /// <param name="handle">The target agent's handle.</param>
    /// <param name="name">Non-empty name the delegating model refers to this agent by. Must be unique within a roster.</param>
    /// <param name="description">What this agent is for, shown to the delegating model.</param>
    /// <param name="timeout">Bound on a single delegation. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="instructionOverlay">Optional text prepended to every delegated instruction.</param>
    /// <param name="messageType">Optional message type stamped on the delegation. Null sends an ordinary request.</param>
    public FabrCoreBackgroundAgent(
        IFabrCoreAgentHost agentHost,
        string handle,
        string name,
        string description,
        TimeSpan? timeout = null,
        string? instructionOverlay = null,
        string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(agentHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        this.agentHost = agentHost;
        this.handle = handle;
        this.instructionOverlay = instructionOverlay;
        this.messageType = messageType;
        this.timeout = timeout ?? DefaultTimeout;

        Name = name;
        Description = description;
    }

    /// <inheritdoc />
    public override string? Name { get; }

    /// <inheritdoc />
    public override string? Description { get; }

    /// <summary>The handle this agent delegates to.</summary>
    public string TargetHandle => handle;

    /// <summary>
    /// Projects the available entries of a roster into delegation agents. Unavailable entries are skipped —
    /// they carry their reason on the roster, and handing an unreachable agent to the model just produces
    /// failed delegations.
    /// </summary>
    public static List<FabrCoreBackgroundAgent> FromRoster(
        AgentRoster roster,
        IFabrCoreAgentHost agentHost,
        TimeSpan? timeout = null,
        string? instructionOverlay = null,
        string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(agentHost);

        return roster.Available
            .Select(entry => new FabrCoreBackgroundAgent(
                agentHost,
                entry.Handle,
                entry.Name,
                entry.Description,
                timeout,
                instructionOverlay,
                messageType))
            .ToList();
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FabrCoreBackgroundAgentSession());

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session.StateBag, jsonSerializerOptions));

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FabrCoreBackgroundAgentSession());

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var instruction = string.Join(
            Environment.NewLine,
            messages.Select(message => message.Text).Where(text => !string.IsNullOrWhiteSpace(text)));

        agentHost.SetStatusMessage($"Delegating to {Name}...");

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(instructionOverlay))
        {
            body.AppendLine(instructionOverlay.Trim());
            body.AppendLine();
        }

        body.Append(instruction);

        var request = new AgentMessage
        {
            FromHandle = agentHost.GetHandle(),
            ToHandle = handle,
            MessageType = messageType,
            Kind = MessageKind.Request,
            Message = body.ToString()
        };

        // The host send exposes no cancellation surface, so bound it here. A breach abandons the wait and
        // surfaces as a failed background task: BackgroundAgentsProvider maps the exception to
        // BackgroundTaskStatus.Failed and the model reads the reason via background_agents_get_task_results.
        var response = await agentHost.SendAndReceiveMessage(request).WaitAsync(timeout, cancellationToken);

        return new AgentResponse([new ChatMessage(ChatRole.Assistant, response.Message ?? string.Empty)
        {
            AuthorName = Name
        }]);
    }

    /// <inheritdoc />
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
}

/// <summary>Session type for <see cref="FabrCoreBackgroundAgent"/>. Carries no state by design.</summary>
public sealed class FabrCoreBackgroundAgentSession : AgentSession
{
    internal FabrCoreBackgroundAgentSession()
    {
    }
}
