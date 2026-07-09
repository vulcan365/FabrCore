using FabrCore.Core.Auditing;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// No-op <see cref="IAuditProvider"/> used when audit recording is explicitly disabled
    /// via <c>FabrCoreServerOptions.UseNullAuditProvider()</c>. Records nothing and returns
    /// empty results.
    /// </summary>
    internal sealed class NullAuditProvider : IAuditProvider
    {
        public event Action<AuditEvent>? OnAuditEventRecorded
        {
            add { }
            remove { }
        }

        public AuditOptions Options { get; } = new()
        {
            DefaultLevel = AuditLevel.None,
            Categories = new Dictionary<AuditCategory, AuditLevel>()
        };

        public Task RecordAsync(AuditEvent auditEvent) => Task.CompletedTask;

        public Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null)
            => Task.FromResult(new List<AuditEvent>());

        public Task ClearAsync() => Task.CompletedTask;
    }
}
