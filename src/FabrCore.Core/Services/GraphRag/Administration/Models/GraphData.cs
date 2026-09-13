namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class GraphData
{
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphLink> Links { get; set; } = [];
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }
    public int ChunkCount { get; set; }
    public string? DomainName { get; set; }
    public string? CategoryName { get; set; }
}

public sealed class GraphLink
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string RelationshipType { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public string? Description { get; set; }
}
