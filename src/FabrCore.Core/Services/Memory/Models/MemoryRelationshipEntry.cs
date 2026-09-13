namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A typed, weighted, directed edge between two memory entities in the knowledge graph.
/// Represents how concepts relate — e.g., Job 1 → has_plate → Plate 11001.
/// </summary>
public class MemoryRelationshipEntry
{
    /// <summary>The type/label of this relationship (e.g., "has_plate", "belongs_to", "supersedes").</summary>
    public string RelationshipType { get; set; } = "";

    /// <summary>Optional description of this relationship.</summary>
    public string? Description { get; set; }

    /// <summary>Confidence/strength weight (0.0 to 1.0). Default: 1.0.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Extensible metadata (serialized as JSON).</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>The entity on the other end of this relationship.</summary>
    public Guid RelatedEntityId { get; set; }

    /// <summary>Title of the related entity (loaded from the JOIN).</summary>
    public string? RelatedEntityTitle { get; set; }

    /// <summary>Type of the related entity.</summary>
    public MemoryType? RelatedEntityType { get; set; }

    /// <summary>When this relationship was created.</summary>
    public DateTime CreatedAt { get; set; }
}
