namespace FabrCore.Services.GraphRag.Services;

public sealed class SourceDocumentDto
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string SourceKind { get; set; } = "Markdown";
    public string SourceKey { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public DateTime? SourceOccurredAtUtc { get; set; }
    public string? MetadataJson { get; set; }
    public long FileSizeBytes { get; set; }
    public Guid? EntityId { get; set; }
    public int ChunkCount { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public int ExtractedEntityCount { get; set; }
    public int ExtractedRelationshipCount { get; set; }
    public string? ContentHash { get; set; }
    public string? InstructionHash { get; set; }
    public int VersionNumber { get; set; } = 1;

    /// <summary>
    /// True when re-ingest was short-circuited because both the content and
    /// extraction-instruction hashes matched the prior successful ingest.
    /// </summary>
    public bool Reused { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
