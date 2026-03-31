namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Pluggable provider for agent access control.
    /// The default implementation (<c>InMemoryAclProvider</c>) loads rules from configuration
    /// and evaluates them in memory. Swap with a custom implementation (database-backed, etc.)
    /// via <c>FabrCoreServerOptions.UseAclProvider&lt;T&gt;()</c>.
    /// </summary>
    public interface IAclProvider
    {
        // ── Evaluation ──

        /// <summary>
        /// Evaluates whether <paramref name="callerOwner"/> has the <paramref name="required"/> permission
        /// on the agent identified by <paramref name="targetOwner"/>:<paramref name="agentAlias"/>.
        /// </summary>
        Task<AclEvaluationResult> EvaluateAsync(
            string callerOwner,
            string targetOwner,
            string agentAlias,
            AclPermission required);

        // ── Rule Management ──

        /// <summary>Gets all configured ACL rules.</summary>
        Task<List<AclRule>> GetRulesAsync();

        /// <summary>Adds a new ACL rule.</summary>
        Task AddRuleAsync(AclRule rule);

        /// <summary>Removes an ACL rule.</summary>
        Task RemoveRuleAsync(AclRule rule);

        // ── Group Management ──

        /// <summary>Gets all groups and their members.</summary>
        Task<Dictionary<string, HashSet<string>>> GetGroupsAsync();

        /// <summary>Adds a member to a group. Creates the group if it doesn't exist.</summary>
        Task AddToGroupAsync(string groupName, string member);

        /// <summary>Removes a member from a group.</summary>
        Task RemoveFromGroupAsync(string groupName, string member);
    }
}
