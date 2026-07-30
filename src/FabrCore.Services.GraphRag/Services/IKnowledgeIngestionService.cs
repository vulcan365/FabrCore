namespace FabrCore.Services.GraphRag.Services;

public interface IKnowledgeIngestionService
{
    Task<SourceDocumentDto> IngestDocumentAsync(KnowledgeIngestionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SourceDocumentDto>> ListDocumentsAsync(string? scopeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<int> CountDocumentsAsync(string? scopeFilter = null, CancellationToken ct = default);
    Task<SourceDocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns every provenance record for the document — one row per entity,
    /// relationship, domain, category, BelongsTo edge, and EXTRACTED_FROM edge
    /// that this document contributed to the graph. Useful for UI diagnostics
    /// and verifying orphan-sweep behavior.
    /// </summary>
    Task<IReadOnlyList<ContributionKey>> GetContributionsAsync(Guid documentId, CancellationToken ct = default);
}
