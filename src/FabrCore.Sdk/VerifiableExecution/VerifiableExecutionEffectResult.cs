using FabrCore.Core.VerifiableExecution;

namespace FabrCore.Sdk.VerifiableExecution;

public sealed class VerifiableExecutionEffectResult<T>
{
    public T? Value { get; init; }
    public VerifiableExecutionEnvelope? Envelope { get; init; }
    public bool EvidenceRecorded => Envelope is not null;
}
