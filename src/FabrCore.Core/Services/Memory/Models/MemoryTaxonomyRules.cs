namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Validates memory entries against the configured taxonomy.
/// Enforces that the memory type is in the allowed set.
/// Content validation is intentionally minimal — this is a general-purpose
/// memory library, not specific to any domain.
/// </summary>
public static class MemoryTaxonomyRules
{
    /// <summary>
    /// Validates that a memory entry conforms to the taxonomy rules.
    /// </summary>
    /// <param name="type">The memory type to validate.</param>
    /// <param name="content">The content to store (not inspected — content policy is the caller's responsibility).</param>
    /// <param name="allowedTypes">The set of allowed memory types (from options).</param>
    /// <returns>A tuple of (IsValid, RejectionReason). RejectionReason is null when valid.</returns>
    public static (bool IsValid, string? RejectionReason) Validate(
        MemoryType type,
        string? content,
        IReadOnlySet<MemoryType> allowedTypes)
    {
        if (!allowedTypes.Contains(type))
            return (false, $"Memory type '{type}' is not in the allowed set: [{string.Join(", ", allowedTypes)}].");

        return (true, null);
    }
}
