namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A registered memory scope — the partition key all memories belong to.
/// An agent's default scope is its own handle (isolated memory). Named shared
/// scopes (e.g. "bank-recon") let multiple agents read and write one memory pool.
/// </summary>
public class MemoryScope
{
    /// <summary>The unique scope key. For isolated agents this is the agent handle.</summary>
    public string ScopeKey { get; set; } = "";

    /// <summary>Optional human-readable description of what this scope holds.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True when the scope was explicitly created as a shared pool; false for scopes
    /// auto-registered from an agent handle on first write. Informational only.
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>When the scope row was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Who created the scope (admin user id or agent handle), when known.</summary>
    public string? CreatedBy { get; set; }
}
