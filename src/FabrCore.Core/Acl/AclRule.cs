namespace FabrCore.Core.Acl
{
    /// <summary>
    /// A single ACL rule defining who can access which agents.
    /// <para>
    /// <strong>Pattern matching:</strong>
    /// <list type="bullet">
    ///   <item><c>"*"</c> — matches anything.</item>
    ///   <item><c>"prefix*"</c> — starts-with match (e.g., <c>"automation_*"</c> matches <c>"automation_agent-123"</c>).</item>
    ///   <item><c>"group:name"</c> — expands to members of the named group (CallerPattern only).</item>
    ///   <item>Exact string — case-insensitive literal match.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class AclRule
    {
        /// <summary>
        /// The owner whose agents are being accessed (e.g., <c>"system"</c>, <c>"alice"</c>, <c>"*"</c>).
        /// </summary>
        public required string OwnerPattern { get; set; }

        /// <summary>
        /// The agent alias pattern (e.g., <c>"*"</c>, <c>"assistant"</c>, <c>"automation_*"</c>).
        /// </summary>
        public required string AgentPattern { get; set; }

        /// <summary>
        /// Who is allowed access (e.g., <c>"*"</c>, <c>"alice"</c>, <c>"group:admins"</c>).
        /// </summary>
        public required string CallerPattern { get; set; }

        /// <summary>
        /// The permissions granted by this rule.
        /// </summary>
        public AclPermission Permission { get; set; }
    }
}
