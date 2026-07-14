using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Persistent state for PrincipalGrain containing tracked agents and pending messages.
    /// </summary>
    public class PrincipalGrainState
    {
        /// <summary>
        /// Agents created and tracked by this principal, keyed by agent handle.
        /// </summary>
        public Dictionary<string, TrackedAgentInfo> TrackedAgents { get; set; } = new();

        /// <summary>
        /// Messages queued while no observers were subscribed.
        /// </summary>
        public List<AgentMessage> PendingMessages { get; set; } = new();

        /// <summary>
        /// Timestamp when pending messages were persisted, used for age-based expiry.
        /// </summary>
        public DateTime? PendingMessagesPersisted { get; set; }

        /// <summary>
        /// Timestamp of the last modification to the state.
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>Bounded provider metadata keyed by a provider-owned namespace.</summary>
        internal Dictionary<string, string> ContextValues { get; set; } = new();

        /// <summary>Durable external delivery work, ordered per principal.</summary>
        internal List<PrincipalDeliveryOutboxEntry> DeliveryOutbox { get; set; } = new();

        /// <summary>Bounded diagnostic history of permanently failed delivery work.</summary>
        internal List<PrincipalDeliveryDeadLetter> DeliveryDeadLetters { get; set; } = new();
    }

    [GenerateSerializer]
    internal struct PrincipalGrainStateSurrogate
    {
        public PrincipalGrainStateSurrogate()
        {
        }

        [Id(0)]
        public Dictionary<string, TrackedAgentInfo> TrackedAgents { get; set; } = new();

        [Id(1)]
        public List<AgentMessage> PendingMessages { get; set; } = new();

        [Id(2)]
        public DateTime? PendingMessagesPersisted { get; set; }

        [Id(3)]
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        [Id(4)]
        public Dictionary<string, string> ContextValues { get; set; } = new();

        [Id(5)]
        public List<PrincipalDeliveryOutboxEntry> DeliveryOutbox { get; set; } = new();

        [Id(6)]
        public List<PrincipalDeliveryDeadLetter> DeliveryDeadLetters { get; set; } = new();
    }

    [RegisterConverter]
    internal sealed class PrincipalGrainStateSurrogateConverter : IConverter<PrincipalGrainState, PrincipalGrainStateSurrogate>
    {
        public PrincipalGrainStateSurrogateConverter()
        {
        }

        public PrincipalGrainState ConvertFromSurrogate(in PrincipalGrainStateSurrogate surrogate)
        {
            return new PrincipalGrainState
            {
                TrackedAgents = surrogate.TrackedAgents ?? new(),
                PendingMessages = surrogate.PendingMessages ?? new(),
                PendingMessagesPersisted = surrogate.PendingMessagesPersisted,
                LastModified = surrogate.LastModified,
                ContextValues = surrogate.ContextValues ?? new(),
                DeliveryOutbox = surrogate.DeliveryOutbox ?? new(),
                DeliveryDeadLetters = surrogate.DeliveryDeadLetters ?? new()
            };
        }

        public PrincipalGrainStateSurrogate ConvertToSurrogate(in PrincipalGrainState value)
        {
            return new PrincipalGrainStateSurrogate
            {
                TrackedAgents = value.TrackedAgents,
                PendingMessages = value.PendingMessages,
                PendingMessagesPersisted = value.PendingMessagesPersisted,
                LastModified = value.LastModified,
                ContextValues = value.ContextValues,
                DeliveryOutbox = value.DeliveryOutbox,
                DeliveryDeadLetters = value.DeliveryDeadLetters
            };
        }
    }

    [GenerateSerializer]
    internal sealed class PrincipalDeliveryOutboxEntry
    {
        [Id(0)] public string DeliveryId { get; set; } = Guid.NewGuid().ToString("N");
        [Id(1)] public AgentMessage Message { get; set; } = new();
        [Id(2)] public string Channel { get; set; } = string.Empty;
        [Id(3)] public string EndpointId { get; set; } = string.Empty;
        [Id(4)] public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        [Id(5)] public DateTimeOffset AvailableAfterUtc { get; set; } = DateTimeOffset.UtcNow;
        [Id(6)] public DateTimeOffset? LeaseExpiresUtc { get; set; }
        [Id(7)] public int AttemptCount { get; set; }
        [Id(8)] public string? LastError { get; set; }
        [Id(9)] public bool WaitingForEndpointRefresh { get; set; }
    }

    [GenerateSerializer]
    internal sealed class PrincipalDeliveryDeadLetter
    {
        [Id(0)] public string DeliveryId { get; set; } = string.Empty;
        [Id(1)] public AgentMessage Message { get; set; } = new();
        [Id(2)] public string Channel { get; set; } = string.Empty;
        [Id(3)] public string EndpointId { get; set; } = string.Empty;
        [Id(4)] public DateTimeOffset CreatedUtc { get; set; }
        [Id(5)] public DateTimeOffset DeadLetteredUtc { get; set; } = DateTimeOffset.UtcNow;
        [Id(6)] public int AttemptCount { get; set; }
        [Id(7)] public string? Reason { get; set; }
    }
}
