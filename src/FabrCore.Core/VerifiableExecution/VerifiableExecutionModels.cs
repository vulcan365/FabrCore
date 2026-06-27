using Orleans;

namespace FabrCore.Core.VerifiableExecution;

public enum ExecutionRecordKind
{
    MessageInbound = 0,
    MessageOutbound = 1,
    EventPublished = 2,
    EventDelivered = 3,
    EventHandled = 4,
    AgentDispatch = 5,
    AgentResponse = 6,
    LlmCall = 7,
    ToolCall = 8,
    PluginCall = 9,
    ExternalHttpCall = 10,
    ExternalDbEffect = 11,
    ExternalStorageEffect = 12,
    Error = 13,
    Terminal = 14
}

public enum ExecutionPropagationScope
{
    None = 0,
    OwnHistory = 1,
    Lineage = 2
}

public enum ExecutionTrustLevel
{
    Unsigned = 0,
    SignedCustomIdentity = 1,
    SignedSpiffeIdentity = 2,
    Verified = 3,
    Tampered = 4,
    ConfigurationError = 5,
    UnverifiableLegacy = 6
}

public enum VerifiableExecutionSignerIdentityKind
{
    None = 0,
    LocalCertificate = 1,
    Certificate = 2,
    Kms = 3,
    Spiffe = 4,
    Custom = 5
}

[GenerateSerializer]
public sealed class VerifiableExecutionEnvelope
{
    [Id(0)]
    public string? TraceId { get; set; }

    [Id(1)]
    public string? RecordId { get; set; }

    [Id(2)]
    public string? PreviousSignatureDigest { get; set; }

    [Id(3)]
    public string? CurrentSignatureDigest { get; set; }

    [Id(4)]
    public string? SignerIdentity { get; set; }

    [Id(5)]
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind { get; set; }

    [Id(6)]
    public string? EvidenceBundleHash { get; set; }

    [Id(7)]
    public string? EvidenceBundleUri { get; set; }

    [Id(8)]
    public List<PropagatedExecutionChunk> Lineage { get; set; } = new();
}

[GenerateSerializer]
public sealed class PropagatedExecutionChunk
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Id(1)]
    public string? TraceId { get; set; }

    [Id(2)]
    public string? SegmentId { get; set; }

    [Id(3)]
    public string? SignatureDigest { get; set; }

    [Id(4)]
    public string? SignerIdentity { get; set; }

    [Id(5)]
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind { get; set; }

    [Id(6)]
    public string? BundleHash { get; set; }
}

public sealed class VerifiableExecutionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public string SegmentId { get; set; } = "default";
    public long Sequence { get; set; }
    public ExecutionRecordKind Kind { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? ParentRecordId { get; set; }
    public string? UserHandle { get; set; }
    public string? AgentHandle { get; set; }
    public string? AgentType { get; set; }
    public string? Subject { get; set; }
    public string? PayloadHash { get; set; }
    public Dictionary<string, string?> Metadata { get; set; } = new(StringComparer.Ordinal);
    public RuntimeProvenance Runtime { get; set; } = new();
}

public sealed class RuntimeProvenance
{
    public string? FabrCoreHostVersion { get; set; }
    public string? FabrCoreCoreVersion { get; set; }
    public string? GitCommit { get; set; }
    public string? ContainerImageDigest { get; set; }
    public string? DeploymentId { get; set; }
    public string? NodeName { get; set; }
    public string? AgentAssembly { get; set; }
    public string? AgentAssemblyVersion { get; set; }
    public string? AgentConfigHash { get; set; }
    public string? SystemPromptHash { get; set; }
    public string? ModelConfigHash { get; set; }
}

public sealed class VerifiableExecutionSignature
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TraceId { get; set; }
    public string? RecordId { get; set; }
    public string? SegmentId { get; set; }
    public long Sequence { get; set; }
    public string? PreviousSignatureDigest { get; set; }
    public string RecordDigest { get; set; } = string.Empty;
    public string SignatureDigest { get; set; } = string.Empty;
    public string? SignerIdentity { get; set; }
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public string? CertificateChainDigest { get; set; }
    public byte[] Signature { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class VerifiableExecutionCertificate
{
    public string Digest { get; set; } = string.Empty;
    public List<byte[]> DerChain { get; set; } = new();
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
}

public sealed class VerifiableExecutionAttestation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TraceId { get; set; }
    public string? ParentRecordId { get; set; }
    public string? RequestRecordId { get; set; }
    public string? ResponseRecordId { get; set; }
    public string? RequestDigest { get; set; }
    public string? ResponseDigest { get; set; }
    public string? TerminalStatus { get; set; }
    public string? SignerIdentity { get; set; }
    public string? SignatureDigest { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class VerifiableExecutionBundle
{
    public string? TraceId { get; set; }
    public List<VerifiableExecutionRecord> Records { get; set; } = new();
    public List<VerifiableExecutionSignature> Signatures { get; set; } = new();
    public List<VerifiableExecutionCertificate> Certificates { get; set; } = new();
    public List<VerifiableExecutionAttestation> Attestations { get; set; } = new();
}

public sealed class VerifiableExecutionVerificationResult
{
    public string? TraceId { get; set; }
    public bool IsValid { get; set; }
    public ExecutionTrustLevel TrustLevel { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long VerifiedRecordCount { get; set; }
    public long VerifiedSignatureCount { get; set; }
}
