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
                LastModified = surrogate.LastModified
            };
        }

        public PrincipalGrainStateSurrogate ConvertToSurrogate(in PrincipalGrainState value)
        {
            return new PrincipalGrainStateSurrogate
            {
                TrackedAgents = value.TrackedAgents,
                PendingMessages = value.PendingMessages,
                PendingMessagesPersisted = value.PendingMessagesPersisted,
                LastModified = value.LastModified
            };
        }
    }
}
