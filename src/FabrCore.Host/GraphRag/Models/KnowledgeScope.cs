namespace FabrCore.Services.GraphRag.Models;

/// <summary>
/// A registered scope key. Scope is the GraphRAG access boundary —
/// every <c>KnowledgeEntity</c> row carries exactly one <c>ScopeKey</c>,
/// and every search filters by a caller-supplied list of allowed scopes.
/// </summary>
public sealed class KnowledgeScope
{
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }
    public double DefaultPriority { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}
