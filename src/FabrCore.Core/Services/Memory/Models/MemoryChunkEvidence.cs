namespace FabrCore.Services.Memory.Models;

/// <summary>Bounded recalled evidence and its source identity. Not an immutable source revision.</summary>
public sealed record MemoryChunkEvidence(Guid ChunkId, int ChunkIndex, string Content, bool IsTruncated);
