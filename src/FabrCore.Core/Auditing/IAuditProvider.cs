namespace FabrCore.Core.Auditing
{
    /// <summary>
    /// Pluggable provider for security audit events (ACL decisions, ACL management changes,
    /// boundary crossings, bootstrap). The default implementation (<c>InMemoryAuditProvider</c>)
    /// stores events in a bounded in-memory buffer with FIFO eviction. Swap with a custom
    /// implementation (database, SIEM, event hub, etc.) via
    /// <c>FabrCoreServerOptions.UseAuditProvider&lt;T&gt;()</c>, or disable recording entirely with
    /// <c>UseNullAuditProvider()</c>.
    /// </summary>
    public interface IAuditProvider
    {
        // ── Recording ──

        /// <summary>
        /// Records an audit event. Implementations apply <see cref="AuditOptions.ShouldRecord"/>
        /// filtering — callers emit unconditionally. Must never throw into the caller's path.
        /// </summary>
        Task RecordAsync(AuditEvent auditEvent);

        // ── Queries ──

        /// <summary>Gets recorded events matching the query. Returns most recent events first.</summary>
        Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null);

        // ── Maintenance ──

        /// <summary>Clears all recorded events.</summary>
        Task ClearAsync();

        // ── Notifications ──

        /// <summary>
        /// Raised when a new event is recorded (after level filtering). Subscribe to push audit
        /// activity to a UI or external system. Implementations must ensure subscriber exceptions
        /// do not propagate to the caller.
        /// </summary>
        event Action<AuditEvent>? OnAuditEventRecorded;

        // ── Configuration ──

        /// <summary>Effective audit options (levels, buffer size) for this provider.</summary>
        AuditOptions Options { get; }
    }
}
