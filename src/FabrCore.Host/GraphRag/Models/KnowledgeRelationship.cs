namespace FabrCore.Services.GraphRag.Models;

internal class KnowledgeRelationship
{
    public string ScopeKey { get; set; } = "";
    public string RelationshipType { get; set; } = "";
    public string? Description { get; set; }
    public double Weight { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public string FromEntityName { get; set; } = "";
    public string FromEntityType { get; set; } = "";
    public string ToEntityName { get; set; } = "";
    public string ToEntityType { get; set; } = "";
}
