namespace FabrCore.Services.Memory.Audit;

/// <summary>
/// Writes audit rows to <c>mem.MemoryAuditLog</c>. All methods are best-effort —
/// implementations swallow database errors and only log internally, so audit
/// failures never fail the underlying memory operation.
/// </summary>
public interface IMemoryAuditLog
{
    /// <summary>Low-level write. Returns once the row is inserted (or the failure is logged).</summary>
    Task RecordAsync(MemoryAuditEntry entry, CancellationToken ct = default);

    /// <summary>Convenience wrapper around <see cref="RecordAsync"/>.</summary>
    Task RecordAsync(
        string actionType,
        string scopeKey,
        Guid? memoryId = null,
        string? summary = null,
        string? actorId = null,
        string? payload = null,
        long? durationMs = null,
        CancellationToken ct = default);
}
