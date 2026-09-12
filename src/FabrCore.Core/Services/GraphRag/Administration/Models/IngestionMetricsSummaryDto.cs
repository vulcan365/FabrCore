namespace FabrCore.Services.GraphRag.Administration.Models;

/// <summary>
/// Aggregate view of <c>grag.IngestionMetric</c> for the Metrics tab.
/// All sums are filtered by the active scope and time window.
/// </summary>
public sealed class IngestionMetricsSummaryDto
{
    public long TotalChatInputTokens { get; set; }
    public long TotalChatOutputTokens { get; set; }
    public int TotalChatCalls { get; set; }
    public int TotalIngestionRuns { get; set; }
    public long TotalDurationMs { get; set; }
    public long TotalChatMs { get; set; }
    public long TotalLlmExtractionMs { get; set; }
    public long TotalEmbeddingMs { get; set; }
    public long TotalSqlWriteMs { get; set; }
    public int TotalEmbeddingBatches { get; set; }
    public int TotalExtractionBatches { get; set; }
    public int TotalExtractionRetries { get; set; }
    public int TotalExtractionTruncations { get; set; }
    public double AverageDurationMs => TotalIngestionRuns == 0
        ? 0
        : (double)TotalDurationMs / TotalIngestionRuns;
    public double AverageLlmMsPerDocument => TotalIngestionRuns == 0
        ? 0
        : (double)TotalLlmExtractionMs / TotalIngestionRuns;
    public double OutputTokensPerSecond => TotalChatMs == 0
        ? 0
        : TotalChatOutputTokens * 1000d / TotalChatMs;
    public IReadOnlyList<TopDocumentDto> TopDocuments { get; set; } = Array.Empty<TopDocumentDto>();
    public IReadOnlyList<ReingestAmplificationDto> ReingestAmplifications { get; set; } = Array.Empty<ReingestAmplificationDto>();
}

public sealed class TopDocumentDto
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public int Runs { get; set; }
    public long ChatInputTokens { get; set; }
    public long ChatOutputTokens { get; set; }
    public long TotalTokens => ChatInputTokens + ChatOutputTokens;
    public long TotalDurationMs { get; set; }
    public double AverageDurationMs { get; set; }
    public DateTime LastIngestedAt { get; set; }
}

public sealed class ReingestAmplificationDto
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public int Runs { get; set; }
    public long FirstRunTokens { get; set; }
    public long TotalTokens { get; set; }
}

/// <summary>
/// Per-document token rollup, used to populate the new Tokens column on the
/// Ingestion tab. Returned in bulk for a list of DocumentIds.
/// </summary>
public sealed class DocumentTokenSummaryDto
{
    public Guid DocumentId { get; set; }
    public long ChatInputTokens { get; set; }
    public long ChatOutputTokens { get; set; }
    public long TotalTokens => ChatInputTokens + ChatOutputTokens;
}
