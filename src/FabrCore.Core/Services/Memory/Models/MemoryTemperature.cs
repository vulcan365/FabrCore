namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Memory temperature determines how a memory is retrieved and loaded into context.
/// Maps to the Visibility column in grag.AgentMemoryEntity.
/// </summary>
public enum MemoryTemperature
{
    /// <summary>Always loaded into agent context. Part of the bounded memory index.</summary>
    Hot,

    /// <summary>On-demand retrieval via LLM relevance selection.</summary>
    Warm,

    /// <summary>Searchable archive. Vector search only, never bulk-loaded.</summary>
    Cold
}
