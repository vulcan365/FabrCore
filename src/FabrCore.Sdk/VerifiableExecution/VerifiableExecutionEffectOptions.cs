namespace FabrCore.Sdk.VerifiableExecution;

public sealed class VerifiableExecutionEffectOptions
{
    public string? Operation { get; set; }
    public string? Target { get; set; }
    public string? Subject { get; set; }
    public string? ComponentType { get; set; }
    public string? ComponentName { get; set; }
    public string? Method { get; set; }
    public string? PayloadHash { get; set; }
    public string? ResultHash { get; set; }
    public IReadOnlyDictionary<string, string?>? Metadata { get; set; }
}
