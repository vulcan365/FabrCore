namespace FabrCore.Core
{
    /// <summary>
    /// Pluggable provider for agent and client registration, tracking, and lifecycle management.
    /// The default implementation (<c>OrleansAgentManagementProvider</c>) delegates to
    /// <c>IAgentManagementGrain</c>. Swap with a custom implementation (MSSQL, Azure Table Storage, etc.)
    /// via <c>FabrCoreServerOptions.UseAgentManagementProvider&lt;T&gt;()</c>.
    /// </summary>
    public interface IAgentManagementProvider
    {
        // ── Registration ──

        /// <summary>Registers an agent as active.</summary>
        Task RegisterAgentAsync(string key, string agentType, string handle);

        /// <summary>Marks an agent as deactivated with a reason.</summary>
        Task DeactivateAgentAsync(string key, string reason);

        /// <summary>Registers a client as active.</summary>
        Task RegisterClientAsync(string clientId);

        /// <summary>Marks a client as deactivated with a reason.</summary>
        Task DeactivateClientAsync(string clientId, string reason);

        // ── Queries ──

        /// <summary>Gets all registered agents and clients.</summary>
        Task<List<AgentInfo>> GetAllAsync();

        /// <summary>Gets agents/clients filtered by status.</summary>
        Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status);

        /// <summary>Gets a single agent/client by its key.</summary>
        Task<AgentInfo?> GetByKeyAsync(string key);

        /// <summary>Gets agents/clients filtered by entity type and optional status.</summary>
        Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null);

        // ── Maintenance ──

        /// <summary>Removes deactivated entries older than the specified age. Returns the count purged.</summary>
        Task<int> PurgeDeactivatedAsync(TimeSpan olderThan);

        /// <summary>Gets aggregate statistics (total, active, deactivated, by type).</summary>
        Task<Dictionary<string, int>> GetStatisticsAsync();
    }
}
