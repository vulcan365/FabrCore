namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminScopeDto
{
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }
    public double DefaultPriority { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public int EntityCount { get; set; }
}
