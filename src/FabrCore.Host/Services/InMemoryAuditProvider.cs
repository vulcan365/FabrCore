using FabrCore.Core.Auditing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default in-memory implementation of <see cref="IAuditProvider"/>.
    /// Stores audit events in a bounded FIFO buffer sized by
    /// <see cref="AuditOptions.MaxBufferedEvents"/>. Events are filtered through
    /// <see cref="AuditOptions.ShouldRecord"/> before buffering.
    /// </summary>
    public class InMemoryAuditProvider : IAuditProvider
    {
        private readonly ConcurrentQueue<AuditEvent> _events = new();
        private readonly ILogger<InMemoryAuditProvider> _logger;
        private int _count;

        public event Action<AuditEvent>? OnAuditEventRecorded;

        /// <inheritdoc />
        public AuditOptions Options { get; }

        public InMemoryAuditProvider(ILogger<InMemoryAuditProvider> logger, IOptions<AuditOptions> options)
        {
            _logger = logger;
            Options = options.Value;
        }

        public Task RecordAsync(AuditEvent auditEvent)
        {
            if (!Options.ShouldRecord(auditEvent.Category, auditEvent.Outcome))
                return Task.CompletedTask;

            _events.Enqueue(auditEvent);
            var currentCount = Interlocked.Increment(ref _count);

            // FIFO eviction
            while (currentCount > Options.MaxBufferedEvents && _events.TryDequeue(out _))
            {
                currentCount = Interlocked.Decrement(ref _count);
            }

            // Fire notification — one throwing subscriber must not prevent siblings from running.
            SafeInvoke(OnAuditEventRecorded, auditEvent);

            return Task.CompletedTask;
        }

        public Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null)
        {
            IEnumerable<AuditEvent> result = _events.ToArray().Reverse();

            if (query is not null)
            {
                if (query.Category.HasValue)
                    result = result.Where(e => e.Category == query.Category.Value);

                if (query.Outcome.HasValue)
                    result = result.Where(e => e.Outcome == query.Outcome.Value);

                if (!string.IsNullOrEmpty(query.SubjectPrincipal))
                    result = result.Where(e =>
                        string.Equals(e.SubjectPrincipal, query.SubjectPrincipal, StringComparison.OrdinalIgnoreCase));

                if (query.Since.HasValue)
                    result = result.Where(e => e.Timestamp >= query.Since.Value);

                if (query.Limit.HasValue)
                    result = result.Take(query.Limit.Value);
            }

            return Task.FromResult(result.ToList());
        }

        public Task ClearAsync()
        {
            while (_events.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _count, 0);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Invokes each subscriber in the multicast delegate individually, so one
        /// throwing subscriber can't prevent later ones from running.
        /// </summary>
        private void SafeInvoke(Action<AuditEvent>? handler, AuditEvent arg)
        {
            if (handler is null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<AuditEvent>)d).Invoke(arg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnAuditEventRecorded subscriber threw an exception");
                }
            }
        }
    }
}
