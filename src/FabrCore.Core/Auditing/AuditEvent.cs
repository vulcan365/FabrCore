namespace FabrCore.Core.Auditing
{
    /// <summary>
    /// Category of a recorded security audit event. Categories can be individually
    /// configured to different <see cref="AuditLevel"/>s via <see cref="AuditOptions.Categories"/>.
    /// </summary>
    public enum AuditCategory
    {
        /// <summary>An ACL authorization decision (message send, read, reconfigure, etc.).</summary>
        AclDecision,

        /// <summary>A change to ACL entities (principals, roles, groups, grants) or ACL configuration.</summary>
        AclManagement,

        /// <summary>An agent-creation authorization check.</summary>
        AgentCreation,

        /// <summary>A message chain crossed (or fanned out beyond) a principal boundary.</summary>
        BoundaryCrossing,

        /// <summary>ACL bootstrap activity (seeding built-ins, applying config seeds).</summary>
        Bootstrap
    }

    /// <summary>Outcome of the audited operation.</summary>
    public enum AuditOutcome
    {
        /// <summary>The operation was allowed / succeeded.</summary>
        Success,

        /// <summary>The operation was denied by policy.</summary>
        Denied,

        /// <summary>The operation failed with an error.</summary>
        Error
    }

    /// <summary>
    /// A single security audit record. Produced by ACL enforcement points, the ACL
    /// management API, and bootstrap, and recorded through <see cref="IAuditProvider"/>.
    /// </summary>
    public class AuditEvent
    {
        /// <summary>Unique id for this event.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("n");

        /// <summary>When the event occurred (UTC).</summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Event category.</summary>
        public AuditCategory Category { get; set; }

        /// <summary>Event outcome.</summary>
        public AuditOutcome Outcome { get; set; }

        /// <summary>Principal handle of the caller/subject that performed the operation.</summary>
        public string? SubjectPrincipal { get; set; }

        /// <summary>Full agent handle ("principal:agent") of the acting agent, when the subject acted through an agent.</summary>
        public string? SubjectAgent { get; set; }

        /// <summary>Principal handle that owns the resource being accessed.</summary>
        public string? ResourcePrincipal { get; set; }

        /// <summary>The resource being accessed (full agent handle, "acl", group name, etc.).</summary>
        public string? Resource { get; set; }

        /// <summary>The permission action that was evaluated, e.g. "agent.message".</summary>
        public string? Permission { get; set; }

        /// <summary>The ACL enforcement mode in effect ("Disabled" | "AuditOnly" | "Enforce").</summary>
        public string? EnforcementMode { get; set; }

        /// <summary>
        /// True when a denial was actually enforced (the operation was blocked).
        /// False for AuditOnly/Disabled would-be denials and dry-run evaluations.
        /// </summary>
        public bool WasEnforced { get; set; }

        /// <summary>Human-readable reason: matched grant, "no matching grant", "system bypass", etc.</summary>
        public string? Reason { get; set; }

        /// <summary>Additional structured details (message id, hop count, changed entity, etc.).</summary>
        public Dictionary<string, string> Details { get; set; } = new();

        /// <summary>W3C trace id from <c>Activity.Current</c>, for joining with OpenTelemetry traces.</summary>
        public string? TraceId { get; set; }

        /// <summary>Optional verifiable-execution trace id when verifiable execution is enabled.</summary>
        public string? VerifiableExecutionId { get; set; }
    }

    /// <summary>Filter for querying recorded audit events.</summary>
    public class AuditQuery
    {
        /// <summary>Only events in this category.</summary>
        public AuditCategory? Category { get; set; }

        /// <summary>Only events with this outcome.</summary>
        public AuditOutcome? Outcome { get; set; }

        /// <summary>Only events whose subject principal matches (case-insensitive).</summary>
        public string? SubjectPrincipal { get; set; }

        /// <summary>Only events at or after this time.</summary>
        public DateTimeOffset? Since { get; set; }

        /// <summary>Maximum number of events to return (most recent first).</summary>
        public int? Limit { get; set; }
    }
}
