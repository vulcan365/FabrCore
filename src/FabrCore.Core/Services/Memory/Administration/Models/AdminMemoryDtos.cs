using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Administration.Models;

/// <summary>List-row projection of a memory for admin tables. No content or embeddings.</summary>
public sealed class AdminMemoryDto
{
    public Guid MemoryId { get; set; }
    public string ScopeKey { get; set; } = "";
    public string Title { get; set; } = "";
    public MemoryType Type { get; set; }
    public MemoryTemperature Temperature { get; set; }
    public bool IsPointInTime { get; set; }
    public string? Description { get; set; }
    public int ChunkCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Full memory detail: content, raw metadata JSON, chunks, and relationships.</summary>
public sealed class AdminMemoryDetailDto
{
    public Guid MemoryId { get; set; }
    public string ScopeKey { get; set; } = "";
    public string Title { get; set; } = "";
    public MemoryType Type { get; set; }
    public MemoryTemperature Temperature { get; set; }
    public bool IsPointInTime { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Primary chunk content (ChunkIndex = 0).</summary>
    public string? Content { get; set; }

    /// <summary>Raw metadata JSON for display.</summary>
    public string? Metadata { get; set; }

    public List<AdminMemoryChunkDto> Chunks { get; set; } = [];
    public List<AdminMemoryRelationshipDto> Relationships { get; set; } = [];
}

/// <summary>One content chunk of a memory.</summary>
public sealed class AdminMemoryChunkDto
{
    public Guid ChunkId { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = "";
    public bool HasEmbedding { get; set; }
}

/// <summary>A graph edge from or to the viewed memory.</summary>
public sealed class AdminMemoryRelationshipDto
{
    public Guid RelatedMemoryId { get; set; }
    public string RelatedTitle { get; set; } = "";
    public string RelationshipType { get; set; } = "";

    /// <summary>"outgoing" or "incoming", relative to the viewed memory.</summary>
    public string Direction { get; set; } = "";
}
