namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminSearchResult
{
    public string SearchType { get; set; } = "";
    public string RawJson { get; set; } = "[]";
    public int ResultCount { get; set; }
    public string? ErrorMessage { get; set; }
    public long ElapsedMs { get; set; }
}
