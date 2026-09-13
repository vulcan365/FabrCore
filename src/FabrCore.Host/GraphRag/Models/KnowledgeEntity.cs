namespace FabrCore.Services.GraphRag.Models;

internal class KnowledgeEntity
{
    public Guid EntityId { get; set; }
    public Guid CanonicalEntityId { get; set; }
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }
    public string? Content { get; set; }
    public float[]? Embedding { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public double Distance { get; set; }
}
