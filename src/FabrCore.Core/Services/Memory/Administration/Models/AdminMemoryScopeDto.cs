namespace FabrCore.Services.Memory.Administration.Models;

/// <summary>A memory scope as shown in admin tooling, with usage counts.</summary>
public sealed class AdminMemoryScopeDto
{
    public string ScopeKey { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>True when the scope was explicitly created as a shared pool.</summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// True when a mem.MemoryScope row exists; false for scopes that only appear
    /// implicitly through memory rows (e.g. written before registration existed).
    /// </summary>
    public bool IsRegistered { get; set; }

    public DateTime? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>Memory entities in the scope (excludes the internal index sentinel).</summary>
    public int MemoryCount { get; set; }

    /// <summary>Most recent memory update in the scope.</summary>
    public DateTime? LastUpdatedAt { get; set; }
}

/// <summary>Result of a destructive scope delete — what was removed.</summary>
public sealed class AdminScopeDeleteResult
{
    public string ScopeKey { get; set; } = "";
    public int MemoriesDeleted { get; set; }
    public int ChunksDeleted { get; set; }
    public int RelationshipsDeleted { get; set; }
    public int SummaryNodesDeleted { get; set; }
}
