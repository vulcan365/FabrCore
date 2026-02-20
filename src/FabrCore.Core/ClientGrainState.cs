using Orleans;

namespace Fabr.Core
{
    /// <summary>
    /// Persistent state for ClientGrain containing tracked agents and pending messages.
    /// </summary>
    public class ClientGrainState
    {
        /// <summary>
        /// Agents created and tracked by this client, keyed by agent handle.
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
    internal struct ClientGrainStateSurrogate
    {
        public ClientGrainStateSurrogate()
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
    internal sealed class ClientGrainStateSurrogateConverter : IConverter<ClientGrainState, ClientGrainStateSurrogate>
    {
        public ClientGrainStateSurrogateConverter()
        {
        }

        public ClientGrainState ConvertFromSurrogate(in ClientGrainStateSurrogate surrogate)
        {
            return new ClientGrainState
            {
                TrackedAgents = surrogate.TrackedAgents ?? new(),
                PendingMessages = surrogate.PendingMessages ?? new(),
                PendingMessagesPersisted = surrogate.PendingMessagesPersisted,
                LastModified = surrogate.LastModified
            };
        }

        public ClientGrainStateSurrogate ConvertToSurrogate(in ClientGrainState value)
        {
            return new ClientGrainStateSurrogate
            {
                TrackedAgents = value.TrackedAgents,
                PendingMessages = value.PendingMessages,
                PendingMessagesPersisted = value.PendingMessagesPersisted,
                LastModified = value.LastModified
            };
        }
    }
}
