using System.Data;
using System.Text.Json;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Services;
using Microsoft.Data.SqlClient;

namespace FabrCore.Host.Database;

/// <summary>Atomic, append-only evidence and cross-process signature-chain coordination.</summary>
public sealed class SqlVerifiableExecutionStore(OperationalDatabase database) : IVerifiableExecutionStore, IVerifiableExecutionWriteCoordinator
{
    public async ValueTask<IAsyncDisposable> AcquireWriteAsync(string traceId, string segmentId, CancellationToken ct = default)
    {
        var key = JsonSerializer.Serialize(new[] { OperationalDatabase.Key(traceId, nameof(traceId)), OperationalDatabase.Key(segmentId, nameof(segmentId), 256) });
        var connection = await database.OpenCoordinationAsync(ct);
        try { await OperationalDatabase.LockAsync(connection, null, "evidence-chain:" + key, ct); return new Lease(connection, "evidence-chain:" + key); }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class Lease(SqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var resource = "FabrCore.Operations:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
                await using var command = OperationalDatabase.Command(connection, "EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';", null, ("@resource", resource));
                await command.ExecuteNonQueryAsync();
            }
            finally { await connection.DisposeAsync(); }
        }
    }

    public async Task<long> GetNextSequenceAsync(string traceId, string segmentId, CancellationToken cancellationToken = default)
    {
        Validate(traceId, segmentId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = OperationalDatabase.Command(connection, """
            IF NOT EXISTS(SELECT 1 FROM fabrOps.EvidenceSequence WITH(UPDLOCK,HOLDLOCK) WHERE TraceId=@trace AND SegmentId=@segment)
                INSERT fabrOps.EvidenceSequence VALUES(@trace,@segment,0);
            UPDATE fabrOps.EvidenceSequence SET NextSequence=NextSequence+1
            OUTPUT inserted.NextSequence WHERE TraceId=@trace AND SegmentId=@segment;
            """, transaction, ("@trace", traceId), ("@segment", segmentId));
        var result = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken); return result;
    }

    public async Task AppendRecordAsync(VerifiableExecutionRecord record, VerifiableExecutionSignature? signature, VerifiableExecutionCertificate? certificate, CancellationToken cancellationToken = default)
    {
        Validate(record.TraceId!, record.SegmentId); OperationalDatabase.Key(record.Id, nameof(record.Id));
        if (record.Sequence < 1) throw new ArgumentException("Evidence requires a positive sequence.");
        if (signature is not null && (signature.TraceId != record.TraceId || signature.RecordId != record.Id || signature.SegmentId != record.SegmentId || signature.Sequence != record.Sequence ||
            signature.RecordDigest != VerifiableExecutionCanonicalizer.DigestHex(VerifiableExecutionCanonicalizer.CanonicalRecordBytes(record))))
            throw new InvalidOperationException("Signature does not bind to this evidence record.");
        if (certificate is not null && (signature?.CertificateChainDigest != certificate.Digest || certificate.Digest != VerifiableExecutionCanonicalizer.DigestBytes(certificate.DerChain.FirstOrDefault())))
            throw new InvalidOperationException("Certificate does not match the signature.");
        var recordJson = JsonSerializer.Serialize(record); var signatureJson = signature is null ? null : JsonSerializer.Serialize(signature); var certificateJson = certificate is null ? null : JsonSerializer.Serialize(certificate);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Serialize direct appends too; a stale chain predecessor is rejected instead of forking the evidence chain.
        await OperationalDatabase.LockAsync(connection, transaction, "evidence-append:" + JsonSerializer.Serialize(new[] { record.TraceId, record.SegmentId }), cancellationToken);
        await using (var existing = OperationalDatabase.Command(connection, "SELECT RecordJson,SignatureJson,CertificateJson FROM fabrOps.Evidence WHERE TraceId=@trace AND RecordId=@id;", transaction, ("@trace", record.TraceId), ("@id", record.Id)))
        {
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != recordJson || (reader.IsDBNull(1) ? null : reader.GetString(1)) != signatureJson || (reader.IsDBNull(2) ? null : reader.GetString(2)) != certificateJson)
                    throw new InvalidOperationException("An immutable evidence record cannot be replaced.");
                return;
            }
        }
        await using (var latest = OperationalDatabase.Command(connection, "SELECT TOP(1) Sequence,SignatureJson FROM fabrOps.Evidence WHERE TraceId=@trace AND SegmentId=@segment ORDER BY Sequence DESC;", transaction, ("@trace", record.TraceId), ("@segment", record.SegmentId)))
        {
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) >= record.Sequence) throw new InvalidOperationException("Evidence sequence is stale.");
        }
        if (signature is not null)
        {
            var previous = await Latest(connection, transaction, record.TraceId!, record.SegmentId, cancellationToken);
            if (signature.PreviousSignatureDigest != previous?.SignatureDigest) throw new InvalidOperationException("Evidence chain predecessor changed.");
        }
        await using var append = OperationalDatabase.Command(connection, """
            INSERT fabrOps.Evidence(TraceId,SegmentId,Sequence,RecordId,Timestamp,RecordJson,SignatureJson,CertificateJson)
            VALUES(@trace,@segment,@sequence,@id,@time,@record,@signature,@certificate);
            IF NOT EXISTS(SELECT 1 FROM fabrOps.EvidenceSequence WITH(UPDLOCK,HOLDLOCK) WHERE TraceId=@trace AND SegmentId=@segment)
                INSERT fabrOps.EvidenceSequence VALUES(@trace,@segment,@sequence);
            ELSE UPDATE fabrOps.EvidenceSequence SET NextSequence=@sequence
                WHERE TraceId=@trace AND SegmentId=@segment AND NextSequence<@sequence;
            """, transaction, ("@trace", record.TraceId), ("@segment", record.SegmentId), ("@sequence", record.Sequence), ("@id", record.Id), ("@time", record.Timestamp),
            ("@record", recordJson), ("@signature", signatureJson), ("@certificate", certificateJson));
        await append.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddAttestationAsync(VerifiableExecutionAttestation attestation, CancellationToken cancellationToken = default)
    {
        OperationalDatabase.Key(attestation.TraceId!, nameof(attestation.TraceId)); OperationalDatabase.Key(attestation.Id, nameof(attestation.Id));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var json = JsonSerializer.Serialize(attestation);
        await using var command = OperationalDatabase.Command(connection, """
            DECLARE @existing nvarchar(max)=(SELECT Payload FROM fabrOps.Attestation WITH(UPDLOCK,HOLDLOCK) WHERE TraceId=@trace AND Id=@id);
            IF @existing IS NULL INSERT fabrOps.Attestation VALUES(@trace,@id,@json);
            ELSE IF @existing COLLATE Latin1_General_100_BIN2 <> @json COLLATE Latin1_General_100_BIN2 THROW 51000, 'Attestations are immutable.', 1;
            """, transaction, ("@trace", attestation.TraceId), ("@id", attestation.Id), ("@json", json));
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<VerifiableExecutionBundle> GetBundleAsync(string traceId, CancellationToken cancellationToken = default)
    {
        OperationalDatabase.Key(traceId, nameof(traceId));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var bundle = new VerifiableExecutionBundle { TraceId = traceId };
        await using (var command = OperationalDatabase.Command(connection, "SELECT RecordJson,SignatureJson,CertificateJson FROM fabrOps.Evidence WHERE TraceId=@trace ORDER BY SegmentId,Sequence; SELECT Payload FROM fabrOps.Attestation WHERE TraceId=@trace ORDER BY Id;", transaction, ("@trace", traceId)))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bundle.Records.Add(JsonSerializer.Deserialize<VerifiableExecutionRecord>(reader.GetString(0))!);
                if (!reader.IsDBNull(1)) bundle.Signatures.Add(JsonSerializer.Deserialize<VerifiableExecutionSignature>(reader.GetString(1))!);
                if (!reader.IsDBNull(2)) bundle.Certificates.Add(JsonSerializer.Deserialize<VerifiableExecutionCertificate>(reader.GetString(2))!);
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) bundle.Attestations.Add(JsonSerializer.Deserialize<VerifiableExecutionAttestation>(reader.GetString(0))!);
        }
        bundle.Certificates = bundle.Certificates.DistinctBy(c => c.Digest).ToList();
        await transaction.CommitAsync(cancellationToken); return bundle;
    }

    public async Task<VerifiableExecutionSignature?> GetLatestSignatureAsync(string traceId, string segmentId, CancellationToken cancellationToken = default)
    {
        Validate(traceId, segmentId); await using var connection = await database.OpenAsync(cancellationToken);
        return await Latest(connection, null, traceId, segmentId, cancellationToken);
    }
    private static async Task<VerifiableExecutionSignature?> Latest(SqlConnection connection, SqlTransaction? transaction, string trace, string segment, CancellationToken ct)
    {
        await using var command = OperationalDatabase.Command(connection, "SELECT TOP(1) SignatureJson FROM fabrOps.Evidence WHERE TraceId=@trace AND SegmentId=@segment AND SignatureJson IS NOT NULL ORDER BY Sequence DESC;", transaction, ("@trace", trace), ("@segment", segment));
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<VerifiableExecutionSignature>(json) : null;
    }
    private static void Validate(string trace, string segment) { OperationalDatabase.Key(trace, nameof(trace)); OperationalDatabase.Key(segment, nameof(segment), 256); }
}
