namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Describes one document ingestion operation. Extraction instructions guide
/// entity, relationship, and taxonomy extraction but are never added to the
/// stored or searchable Markdown content.
/// </summary>
public sealed record KnowledgeIngestionRequest(
    string FileName,
    string ScopeKey,
    string MarkdownContent,
    string? ExtractionInstructions = null);
