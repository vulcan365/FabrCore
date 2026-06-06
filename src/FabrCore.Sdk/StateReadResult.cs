using System.Text.Json;

namespace FabrCore.Sdk;

/// <summary>
/// Result of an attempted custom state read.
/// </summary>
/// <typeparam name="T">The requested state value type.</typeparam>
public sealed class StateReadResult<T>
{
    public bool Succeeded { get; init; }
    public T? Value { get; init; }
    public string Key { get; init; } = string.Empty;
    public JsonValueKind? ValueKind { get; init; }
    public Exception? Error { get; init; }
}
