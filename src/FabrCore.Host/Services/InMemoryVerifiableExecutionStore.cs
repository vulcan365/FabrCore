using FabrCore.Core.VerifiableExecution;
using System.Collections.Concurrent;

namespace FabrCore.Host.Services;

public sealed class InMemoryVerifiableExecutionStore : IVerifiableExecutionStore
{
    private readonly ConcurrentDictionary<string, List<VerifiableExecutionRecord>> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<VerifiableExecutionSignature>> _signatures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VerifiableExecutionCertificate> _certificates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<VerifiableExecutionAttestation>> _attestations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task AppendRecordAsync(
        VerifiableExecutionRecord record,
        VerifiableExecutionSignature? signature,
        VerifiableExecutionCertificate? certificate,
        CancellationToken cancellationToken = default)
    {
        var traceId = RequireTraceId(record.TraceId);
        lock (_gate)
        {
            _records.GetOrAdd(traceId, _ => new List<VerifiableExecutionRecord>()).Add(record);
            if (signature is not null)
            {
                _signatures.GetOrAdd(traceId, _ => new List<VerifiableExecutionSignature>()).Add(signature);
            }

            if (certificate is not null && !string.IsNullOrEmpty(certificate.Digest))
            {
                _certificates.TryAdd(certificate.Digest, certificate);
            }
        }

        return Task.CompletedTask;
    }

    public Task AddAttestationAsync(VerifiableExecutionAttestation attestation, CancellationToken cancellationToken = default)
    {
        var traceId = RequireTraceId(attestation.TraceId);
        lock (_gate)
        {
            _attestations.GetOrAdd(traceId, _ => new List<VerifiableExecutionAttestation>()).Add(attestation);
        }

        return Task.CompletedTask;
    }

    public Task<VerifiableExecutionBundle> GetBundleAsync(string traceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _records.TryGetValue(traceId, out var records);
            _signatures.TryGetValue(traceId, out var signatures);
            _attestations.TryGetValue(traceId, out var attestations);

            var certificateDigests = signatures?
                .Select(s => s.CertificateChainDigest)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!)
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            return Task.FromResult(new VerifiableExecutionBundle
            {
                TraceId = traceId,
                Records = records?.OrderBy(r => r.Sequence).ToList() ?? new List<VerifiableExecutionRecord>(),
                Signatures = signatures?.OrderBy(s => s.Sequence).ToList() ?? new List<VerifiableExecutionSignature>(),
                Attestations = attestations?.ToList() ?? new List<VerifiableExecutionAttestation>(),
                Certificates = certificateDigests
                    .Where(d => _certificates.ContainsKey(d!))
                    .Select(d => _certificates[d!])
                    .ToList()
            });
        }
    }

    public Task<VerifiableExecutionSignature?> GetLatestSignatureAsync(
        string traceId,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _signatures.TryGetValue(traceId, out var signatures);
            return Task.FromResult(signatures?
                .Where(s => string.Equals(s.SegmentId, segmentId, StringComparison.Ordinal))
                .OrderByDescending(s => s.Sequence)
                .FirstOrDefault());
        }
    }

    public Task<long> GetNextSequenceAsync(string traceId, string segmentId, CancellationToken cancellationToken = default)
    {
        var key = $"{traceId}:{segmentId}";
        var next = _sequences.AddOrUpdate(key, 1, (_, current) => current + 1);
        return Task.FromResult(next);
    }

    private static string RequireTraceId(string? traceId)
        => string.IsNullOrWhiteSpace(traceId)
            ? throw new ArgumentException("TraceId is required for verifiable execution records.")
            : traceId;
}
