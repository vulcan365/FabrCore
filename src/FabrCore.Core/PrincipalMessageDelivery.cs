using Orleans;

namespace FabrCore.Core;

/// <summary>
/// Selects an external delivery channel and, optionally, a provider-owned endpoint.
/// The channel is a provider-neutral identifier such as <c>sms</c>, <c>email</c>,
/// or <c>m365copilot</c>.
/// </summary>
[GenerateSerializer]
public sealed class PrincipalDeliveryTarget
{
    public PrincipalDeliveryTarget()
    {
    }

    public PrincipalDeliveryTarget(string? channel, string? endpointId = null)
    {
        Channel = channel;
        EndpointId = endpointId;
    }

    [Id(0)]
    public string? Channel { get; set; }

    [Id(1)]
    public string? EndpointId { get; set; }
}

/// <summary>Result of asking a relay whether it can deliver a message.</summary>
public enum PrincipalMessageRelayResolutionStatus
{
    NotApplicable = 0,
    Unavailable = 1,
    Available = 2
}

/// <summary>
/// Describes a provider endpoint selected by a relay. Provider payloads remain in
/// the principal context and are not interpreted by FabrCore.
/// </summary>
public sealed class PrincipalMessageRelayResolution
{
    private PrincipalMessageRelayResolution(
        PrincipalMessageRelayResolutionStatus status,
        string? channel,
        string? endpointId,
        DateTimeOffset? lastActiveUtc)
    {
        Status = status;
        Channel = channel;
        EndpointId = endpointId;
        LastActiveUtc = lastActiveUtc;
    }

    public PrincipalMessageRelayResolutionStatus Status { get; }

    public string? Channel { get; }

    public string? EndpointId { get; }

    public DateTimeOffset? LastActiveUtc { get; }

    public static PrincipalMessageRelayResolution NotApplicable() =>
        new(PrincipalMessageRelayResolutionStatus.NotApplicable, null, null, null);

    public static PrincipalMessageRelayResolution Unavailable(string? channel = null) =>
        new(PrincipalMessageRelayResolutionStatus.Unavailable, channel, null, null);

    public static PrincipalMessageRelayResolution Available(
        string channel,
        string endpointId,
        DateTimeOffset lastActiveUtc) =>
        new(PrincipalMessageRelayResolutionStatus.Available, channel, endpointId, lastActiveUtc);
}

/// <summary>
/// Durable work handed to an external relay. Relays must return promptly from
/// <see cref="IPrincipalMessageRelay.TryEnqueueAsync"/> and perform provider I/O
/// outside the principal grain call.
/// </summary>
public sealed class PrincipalMessageDelivery
{
    public required string DeliveryId { get; init; }

    public required string PrincipalHandle { get; init; }

    public required AgentMessage Message { get; init; }

    public required string Channel { get; init; }

    public required string EndpointId { get; init; }

    public required IReadOnlyDictionary<string, string> PrincipalContext { get; init; }

    public int AttemptNumber { get; init; }
}

/// <summary>Terminal or retryable result reported by an external relay.</summary>
public enum PrincipalMessageDeliveryOutcomeKind
{
    Delivered = 0,
    RetryableFailure = 1,
    EndpointUnavailable = 2,
    PermanentFailure = 3
}

/// <summary>A relay's completion result for durable delivery work.</summary>
[GenerateSerializer]
public sealed class PrincipalMessageDeliveryOutcome
{
    public PrincipalMessageDeliveryOutcome()
    {
    }

    private PrincipalMessageDeliveryOutcome(
        PrincipalMessageDeliveryOutcomeKind kind,
        TimeSpan? retryAfter,
        string? error)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        Error = error;
    }

    [Id(0)]
    public PrincipalMessageDeliveryOutcomeKind Kind { get; set; }

    [Id(1)]
    public TimeSpan? RetryAfter { get; set; }

    [Id(2)]
    public string? Error { get; set; }

    public static PrincipalMessageDeliveryOutcome Delivered() =>
        new(PrincipalMessageDeliveryOutcomeKind.Delivered, null, null);

    public static PrincipalMessageDeliveryOutcome RetryableFailure(
        TimeSpan? retryAfter = null,
        string? error = null) =>
        new(PrincipalMessageDeliveryOutcomeKind.RetryableFailure, retryAfter, error);

    public static PrincipalMessageDeliveryOutcome EndpointUnavailable(
        TimeSpan? retryAfter = null,
        string? error = null) =>
        new(PrincipalMessageDeliveryOutcomeKind.EndpointUnavailable, retryAfter, error);

    public static PrincipalMessageDeliveryOutcome PermanentFailure(string? error = null) =>
        new(PrincipalMessageDeliveryOutcomeKind.PermanentFailure, null, error);
}

/// <summary>
/// Provider extension point for resolving and asynchronously accepting principal
/// message delivery work.
/// </summary>
public interface IPrincipalMessageRelay
{
    /// <summary>Stable, case-insensitive channel identifier owned by this relay.</summary>
    string Channel { get; }

    ValueTask<PrincipalMessageRelayResolution> ResolveAsync(
        string principalHandle,
        AgentMessage message,
        PrincipalDeliveryTarget? target,
        IReadOnlyDictionary<string, string> principalContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to place work on a bounded provider queue. Returns false when the
    /// queue cannot currently accept work; FabrCore retains and retries the entry.
    /// </summary>
    ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default);
}

/// <summary>Reports completion of durable relay work back to FabrCore.</summary>
public interface IPrincipalMessageDeliveryCompletion
{
    Task CompleteAsync(
        string principalHandle,
        string deliveryId,
        PrincipalMessageDeliveryOutcome outcome,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores bounded, namespaced provider metadata associated with a principal.
/// Credentials and access tokens must be kept in provider secret storage, not here.
/// </summary>
public interface IPrincipalContextStore
{
    Task SetAsync(
        string principalHandle,
        string key,
        string? value,
        CancellationToken cancellationToken = default);

    Task<string?> GetAsync(
        string principalHandle,
        string key,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        string principalHandle,
        CancellationToken cancellationToken = default);
}
