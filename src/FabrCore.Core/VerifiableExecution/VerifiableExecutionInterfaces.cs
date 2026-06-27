namespace FabrCore.Core.VerifiableExecution;

public interface IVerifiableExecutionStore
{
    Task AppendRecordAsync(
        VerifiableExecutionRecord record,
        VerifiableExecutionSignature? signature,
        VerifiableExecutionCertificate? certificate,
        CancellationToken cancellationToken = default);

    Task AddAttestationAsync(VerifiableExecutionAttestation attestation, CancellationToken cancellationToken = default);

    Task<VerifiableExecutionBundle> GetBundleAsync(string traceId, CancellationToken cancellationToken = default);

    Task<VerifiableExecutionSignature?> GetLatestSignatureAsync(
        string traceId,
        string segmentId,
        CancellationToken cancellationToken = default);

    Task<long> GetNextSequenceAsync(string traceId, string segmentId, CancellationToken cancellationToken = default);
}

public interface IVerifiableExecutionSigner
{
    bool CanSign { get; }
    string? SignerIdentity { get; }
    VerifiableExecutionSignerIdentityKind SignerIdentityKind { get; }
    string? SignatureAlgorithm { get; }
    IReadOnlyList<byte[]> CertificateChain { get; }

    Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default);
}

public interface IVerifiableExecutionVerifier
{
    Task<VerifiableExecutionVerificationResult> VerifyAsync(
        VerifiableExecutionBundle bundle,
        CancellationToken cancellationToken = default);
}

public interface IVerifiableExecutionContext
{
    Task<VerifiableExecutionEnvelope?> RecordAsync(
        VerifiableExecutionRecord record,
        CancellationToken cancellationToken = default);

    Task<VerifiableExecutionEnvelope?> RecordExternalEffectAsync(
        ExecutionRecordKind kind,
        string subject,
        IReadOnlyDictionary<string, string?> metadata,
        CancellationToken cancellationToken = default);

    Task<VerifiableExecutionVerificationResult> VerifyAsync(
        string traceId,
        CancellationToken cancellationToken = default);
}
