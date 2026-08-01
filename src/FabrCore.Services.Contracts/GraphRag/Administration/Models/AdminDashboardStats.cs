namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminDashboardStats
{
    public int TotalScopes { get; set; }
    public int TotalEntities { get; set; }
    public int TotalRelationships { get; set; }
    public int TotalChunks { get; set; }
    public int TotalDomains { get; set; }
    public int TotalCategories { get; set; }
}
