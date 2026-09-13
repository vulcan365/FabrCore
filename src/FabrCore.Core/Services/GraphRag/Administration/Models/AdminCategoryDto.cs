namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminCategoryDto
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool HasEmbedding { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? DomainName { get; set; }
    public int EntityCount { get; set; }
}
