namespace FabrCore.Services.GraphRag.Models;

/// <summary>
/// Global identity only. Sensitive descriptions, content, embeddings, and
/// graph assertions belong to scope-owned <see cref="KnowledgeEntity"/> rows.
/// </summary>
internal sealed class CanonicalEntity
{
    public Guid CanonicalEntityId { get; set; }
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
