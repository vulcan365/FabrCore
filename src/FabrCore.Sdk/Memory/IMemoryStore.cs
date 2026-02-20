namespace FabrCore.Sdk.Memory;

/// <summary>
/// Abstraction for persistent memory storage with vector search capabilities.
/// </summary>
public interface IMemoryStore
{
    Task EnsureInitializedAsync();
    Task WriteAsync(MemoryChunk chunk);
    Task WriteBatchAsync(IReadOnlyList<MemoryChunk> chunks);
    Task<List<MemorySearchResult>> SearchAsync(float[] queryEmbedding, string queryText, int maxResults = 6);
    Task<List<MemorySearchResult>> VectorSearchAsync(float[] queryEmbedding, int maxResults);
    Task DeleteBySourceAsync(string sourcePath);
    Task<List<MemorySearchResult>> GetBySourceAsync(string sourcePath);
    Task<(int TotalChunks, int UniqueSources)> GetStatsAsync();
}
