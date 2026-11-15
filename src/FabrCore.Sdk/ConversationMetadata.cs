#pragma warning disable RS0016 // Public API baseline update is outside this change's permitted scope.
namespace FabrCore.Sdk;

/// <summary>A snapshot of metadata for a tracked conversation channel.</summary>
public sealed class ConversationMetadata
{
    /// <summary>The exact channel identifier.</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>The generated or provisional conversation description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether the description still awaits successful worker generation.</summary>
    public bool IsProvisional { get; init; }

    /// <summary>When the conversation was first tracked, in UTC.</summary>
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>When the metadata was last changed, in UTC.</summary>
    public DateTime UpdatedAtUtc { get; init; }
}
#pragma warning restore RS0016
