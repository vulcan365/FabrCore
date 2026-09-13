namespace FabrCore.Services.GraphRag.Models;

internal class KnowledgeCategory
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public float[]? Embedding { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Populated from JOINs when traversing the hierarchy.</summary>
    public string? DomainName { get; set; }
}
