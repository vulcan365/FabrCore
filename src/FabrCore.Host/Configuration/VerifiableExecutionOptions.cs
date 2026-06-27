using FabrCore.Core.VerifiableExecution;

namespace FabrCore.Host.Configuration;

public sealed class VerifiableExecutionOptions
{
    public bool Enabled { get; set; }
    public ExecutionPropagationScope DefaultPropagationScope { get; set; } = ExecutionPropagationScope.Lineage;
    public bool RequireSignerForTrustedExecution { get; set; }
    public bool RequireSpiffeForDaprParity { get; set; }
    public bool RequireAttestedExternalEffects { get; set; }
    public bool CapturePayloadBytes { get; set; }
    public bool FailOnVerificationError { get; set; } = true;
    public int MaxMetadataValueLength { get; set; } = 4096;
    public Func<string?, string?>? Redact { get; set; }
}
