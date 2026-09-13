using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>Optional storage capability for bounded, distinct, non-cold semantic candidates.</summary>
public interface IMemoryCandidateStore
{
    /// <summary>Bounded nearest embedded chunks, in similarity order, for each non-cold entity in scope.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<MemoryChunkEvidence>>> GetMatchedChunksAsync(string scopeKey,
        float[] embedding, IReadOnlyCollection<Guid> ids, int chunksPerMemory, int charactersPerChunk, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support multiple matched chunks.");

    Task<IReadOnlyDictionary<Guid, MemoryChunkEvidence>> GetMatchedChunkEvidenceAsync(string scopeKey,
        float[] embedding, IReadOnlyCollection<Guid> ids, int charactersPerMemory, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support matched chunk evidence.");

    /// <summary>Read bounded primary-body prefixes for non-cold candidates in this scope.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetCandidatePreviewsAsync(string scopeKey,
        IReadOnlyCollection<Guid> ids, int charactersPerMemory, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support candidate previews.");

    /// <summary>Reserve the closest matches, then fill remaining slots across types.</summary>
    Task<IReadOnlyList<MemoryHeader>> FindHybridCandidateHeadersAsync(string scopeKey, float[] embedding,
        int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support hybrid candidates.");

    /// <summary>Bounded semantic candidates interleaved across memory types. Optional capability.</summary>
    Task<IReadOnlyList<MemoryHeader>> FindDiverseCandidateHeadersAsync(string scopeKey, float[] embedding,
        int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support diverse candidates.");

    Task<IReadOnlyList<MemoryHeader>> FindCandidateHeadersAsync(string scopeKey, float[] embedding,
        int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default);
}
