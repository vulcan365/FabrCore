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
        /// Sends an event to the stream identified by the event Namespace and Channel (fire-and-forget).
        /// Leave Namespace empty to send to the default AgentEvent stream for the target Channel.
        /// </summary>
        [AlwaysInterleave]
        Task SendEvent(EventMessage request);

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
        /// Requires Configure permission for cross-user agents.
        /// </summary>
        /// <param name="handle">The agent handle.</param>
        /// <returns>Health status after reset.</returns>
        [AlwaysInterleave]
        Task<AgentHealthStatus> ResetAgent(string handle);

        /// <summary>
        /// Removes an agent from this client's tracked-agent list.
        /// </summary>
        [AlwaysInterleave]
        Task<bool> UntrackAgent(string handle);

        /// <summary>
        /// Gets the list of agents created by this client.
        /// </summary>
        /// <param name="activate">When true, activates each tracked agent by querying health and includes the health result on each returned item.</param>
        /// <returns>List of tracked agents with handle and type.</returns>
        [AlwaysInterleave]
        Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false);

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
        /// Returns agents under other user handles where the caller has at least Message permission.
        /// </summary>
        [AlwaysInterleave]
        Task<List<AgentInfo>> GetAccessibleSharedAgents();
    }
}
