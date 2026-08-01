namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminEntityDto
{
    public Guid EntityId { get; set; }
    public Guid CanonicalEntityId { get; set; }
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }
    public string? Content { get; set; }
    public bool HasEmbedding { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int ChunkCount { get; set; }
    public string? DomainName { get; set; }
    public string? CategoryName { get; set; }
}
