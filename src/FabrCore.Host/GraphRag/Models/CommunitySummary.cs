namespace FabrCore.Services.GraphRag.Models;

internal class CommunitySummary
{
    public Guid SummaryId { get; set; }
    public Guid CategoryId { get; set; }
    public string? ScopeKey { get; set; }
    public string Summary { get; set; } = "";
    public float[]? Embedding { get; set; }
    public int EntityCount { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Populated from JOINs when traversing the hierarchy.</summary>
    public string? CategoryName { get; set; }

    /// <summary>Populated from JOINs when traversing the hierarchy.</summary>
    public string? DomainName { get; set; }
}
