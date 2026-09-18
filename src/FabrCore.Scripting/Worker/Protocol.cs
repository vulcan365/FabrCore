using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Scripting;

[JsonConverter(typeof(JsonStringEnumConverter<ScriptExecutionStatus>))]
public enum ScriptExecutionStatus
{
    Success, CompilationError, RuntimeError, TimedOut, Cancelled, MemoryLimitExceeded, OutputLimitExceeded, WorkerError
}

public sealed record ScriptArtifact(string Name, byte[] Content);

public sealed record ScriptExecutionResult
{
    public ScriptExecutionStatus Status { get; init; }
    public JsonElement? Value { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyList<ScriptArtifact> Artifacts { get; init; } = [];
}

public sealed record ScriptingEnvironmentInfo(
    string EnvironmentId, IReadOnlyDictionary<string, string> Packages,
    IReadOnlyList<string> Imports, string Instructions, double TimeoutSeconds,
    long MemoryLimitBytes, int MaxOutputCharacters, int MaxArtifactBytes);

internal sealed record WorkerRequest(string Code, JsonElement Input, string[] Imports, int OutputLimit, int ArtifactLimit);
