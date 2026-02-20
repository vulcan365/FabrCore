namespace FabrCore.Sdk.Memory;

/// <summary>
/// A stored document chunk with its embedding and metadata.
/// </summary>
public sealed class MemoryChunk
{
    public int Id { get; set; }
    public required string Content { get; set; }
    public required string SourcePath { get; set; }
    public string? SourceType { get; set; }
    public string? Title { get; set; }
    public int ChunkIndex { get; set; }
    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
    public float[]? Embedding { get; set; }
}

/// <summary>
/// A search result with relevance score.
/// </summary>
public sealed class MemorySearchResult
{
    public required string Content { get; set; }
    public required string SourcePath { get; set; }
    public string? Title { get; set; }
    public int ChunkIndex { get; set; }
    public float Score { get; set; }
}
