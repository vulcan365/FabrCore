using Orleans;
using Orleans.Concurrency;

namespace FabrCore.Core.Interfaces
{
    internal interface IClientGrain : IGrainWithStringKey
    {
        Task Subscribe(IClientGrainObserver observer);
        Task Unsubscribe(IClientGrainObserver observer);
        Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);
        Task SendMessage(AgentMessage request);

        /// <summary>
        /// Sends an event to an agent's AgentEvent stream (fire-and-forget).
        /// If streamName is provided, publishes to the named event stream instead of the agent's default stream.
        /// </summary>
        [AlwaysInterleave]
        Task SendEvent(AgentMessage request, string? streamName = null);

        /// <summary>
        /// Creates a new agent with the specified configuration.
        /// This method is marked as interleaving to allow agent creation
        /// to proceed even while long-running message processing is active.
        /// </summary>
        /// <param name="agentConfiguration">The agent configuration.</param>
        /// <returns>Health status of the created agent.</returns>
        [AlwaysInterleave]
        Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration);

        /// <summary>
        /// Gets the list of agents created by this client.
        /// </summary>
        /// <returns>List of tracked agents with handle and type.</returns>
        [AlwaysInterleave]
        Task<List<TrackedAgentInfo>> GetTrackedAgents();

        /// <summary>
        /// Checks if an agent with the specified handle is tracked by this client.
        /// More efficient than GetTrackedAgents() when only checking existence.
        /// </summary>
        /// <param name="handle">The agent handle (without client prefix).</param>
        /// <returns>True if the agent is tracked, false otherwise.</returns>
        [AlwaysInterleave]
        Task<bool> IsAgentTracked(string handle);
    }
}
