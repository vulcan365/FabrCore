using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Strongly-typed wrapper around the structured procedure data stored in a
/// <see cref="MemoryType.Procedural"/> memory. Serialized to the memory's
/// <see cref="MemoryEntry.Metadata"/> under the <see cref="MetadataKey"/> key
/// so the execution structure survives round-trips through the store without
/// requiring a schema change.
/// </summary>
public class ProceduralSteps
{
    /// <summary>Reserved metadata key on <see cref="MemoryEntry.Metadata"/> for the JSON blob.</summary>
    public const string MetadataKey = "__procedure";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Short natural-language description of <i>when</i> to apply this procedure.
    /// Example: <c>"User asks to onboard a new customer."</c>
    /// </summary>
    public string? TriggerCondition { get; set; }

    /// <summary>The ordered sequence of steps that defines the procedure.</summary>
    public List<ProcedureStep> Steps { get; set; } = [];

    /// <summary>
    /// Names of tools the agent typically uses while executing this procedure. Consumers can surface
    /// these as hints to the planner/LLM; the field is advisory, not enforced.
    /// </summary>
    public List<string>? PreferredTools { get; set; }

    /// <summary>Serialize to the JSON shape stored in metadata.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserialize from the metadata JSON shape. Returns null on malformed input.</summary>
    public static ProceduralSteps? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProceduralSteps>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read procedure data from an entry's metadata, if any.</summary>
    public static ProceduralSteps? FromMetadata(MemoryEntry entry)
    {
        if (entry.Metadata is null || !entry.Metadata.TryGetValue(MetadataKey, out var json))
            return null;
        return TryParse(json);
    }
}

/// <summary>A single step in a procedure.</summary>
public class ProcedureStep
{
    /// <summary>1-based step number. The executor honors this order strictly.</summary>
    public int Order { get; set; }

    /// <summary>Imperative phrase describing the action (e.g., <c>"Query the customer table for active records."</c>).</summary>
    public string Action { get; set; } = "";

    /// <summary>Optional extra context, e.g. input requirements, edge cases, failure handling.</summary>
    public string? Description { get; set; }

    /// <summary>What a successful completion of this step looks like. Used for self-verification.</summary>
    public string? ExpectedOutcome { get; set; }

    /// <summary>Optional name of the tool the agent should prefer for this step.</summary>
    public string? Tool { get; set; }
}
