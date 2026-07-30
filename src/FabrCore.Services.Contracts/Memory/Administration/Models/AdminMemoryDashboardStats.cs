using FabrCore.Services.Memory.Audit;

namespace FabrCore.Services.Memory.Administration.Models;

/// <summary>Headline numbers for the memory administration dashboard.</summary>
public sealed class AdminMemoryDashboardStats
{
    public int TotalScopes { get; set; }
    public int TotalMemories { get; set; }
    public int TotalChunks { get; set; }
    public int TotalRelationships { get; set; }
    public int TotalSummaryNodes { get; set; }

    /// <summary>Memory counts keyed by MemoryType name (e.g. "Fact" → 12).</summary>
    public Dictionary<string, int> MemoriesByType { get; set; } = [];

    /// <summary>Memory counts keyed by temperature name (e.g. "Warm" → 40).</summary>
    public Dictionary<string, int> MemoriesByTemperature { get; set; } = [];

    /// <summary>Most recent audit entries across all scopes.</summary>
    public List<MemoryAuditEntry> RecentActivity { get; set; } = [];
}
