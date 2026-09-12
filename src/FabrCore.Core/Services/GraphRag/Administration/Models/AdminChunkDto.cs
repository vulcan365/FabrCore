namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminChunkDto
{
    public Guid ChunkId { get; set; }
    public Guid EntityId { get; set; }
    public string ScopeKey { get; set; } = "";
    public string Content { get; set; } = "";
    public bool HasEmbedding { get; set; }
    public int ChunkIndex { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}
