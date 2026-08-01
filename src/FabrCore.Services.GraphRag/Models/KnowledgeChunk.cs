namespace FabrCore.Services.GraphRag.Models;

internal class KnowledgeChunk
{
    public Guid ChunkId { get; set; }
    public Guid EntityId { get; set; }
    public string ScopeKey { get; set; } = "";
    public string Content { get; set; } = "";
    public float[]? Embedding { get; set; }
    public int ChunkIndex { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public double Distance { get; set; }
}
