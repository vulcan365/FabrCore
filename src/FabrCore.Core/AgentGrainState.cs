using Orleans;
using System.Text.Json;

namespace Fabr.Core
{
    /// <summary>
    /// Persistent state for AgentGrain containing configuration and message threads.
    /// </summary>
    public class AgentGrainState
    {
        /// <summary>
        /// The agent configuration, persisted for restoration on grain reactivation.
        /// </summary>
        public AgentConfiguration? Configuration { get; set; }

        /// <summary>
        /// Message threads keyed by thread ID.
        /// </summary>
        public Dictionary<string, List<StoredChatMessage>> MessageThreads { get; set; } = new();

        /// <summary>
        /// Timestamp of the last modification to the state.
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Developer custom state stored as JSON elements keyed by name.
        /// Values are serialized using System.Text.Json.
        /// </summary>
        public Dictionary<string, JsonElement> CustomState { get; set; } = new();
    }

    [GenerateSerializer]
    internal struct AgentGrainStateSurrogate
    {
        public AgentGrainStateSurrogate()
        {
        }

        [Id(0)]
        public AgentConfiguration? Configuration { get; set; }

        [Id(1)]
        public Dictionary<string, List<StoredChatMessage>> MessageThreads { get; set; } = new();

        [Id(2)]
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        [Id(3)]
        public Dictionary<string, JsonElement> CustomState { get; set; } = new();
    }

    [RegisterConverter]
    internal sealed class AgentGrainStateSurrogateConverter : IConverter<AgentGrainState, AgentGrainStateSurrogate>
    {
        public AgentGrainStateSurrogateConverter()
        {
        }

        public AgentGrainState ConvertFromSurrogate(in AgentGrainStateSurrogate surrogate)
        {
            return new AgentGrainState
            {
                Configuration = surrogate.Configuration,
                MessageThreads = surrogate.MessageThreads ?? new(),
                LastModified = surrogate.LastModified,
                CustomState = surrogate.CustomState ?? new()
            };
        }

        public AgentGrainStateSurrogate ConvertToSurrogate(in AgentGrainState value)
        {
            return new AgentGrainStateSurrogate
            {
                Configuration = value.Configuration,
                MessageThreads = value.MessageThreads,
                LastModified = value.LastModified,
                CustomState = value.CustomState
            };
        }
    }
}
