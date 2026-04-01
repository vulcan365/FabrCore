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
        Task SendEvent(EventMessage request, string? streamName = null);

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
        /// Resets an agent's state and reconfigures it.
        /// Requires Configure permission for cross-owner agents.
        /// </summary>
        /// <param name="handle">The agent handle.</param>
        /// <returns>Health status after reset.</returns>
        [AlwaysInterleave]
        Task<AgentHealthStatus> ResetAgent(string handle);

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

        /// <summary>
        /// Gets shared agents that this client has permission to access (via ACL).
        /// Returns agents owned by other owners where the caller has at least Message permission.
        /// </summary>
        [AlwaysInterleave]
        Task<List<AgentInfo>> GetAccessibleSharedAgents();
    }
}
