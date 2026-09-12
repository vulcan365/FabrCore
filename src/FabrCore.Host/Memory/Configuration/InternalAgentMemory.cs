using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Services;

namespace FabrCore.Services.Memory.Configuration;

public enum InternalAgentMemoryMode { CoreOnly, CoreAndOwn, OwnOnly }

/// <summary>Trusted host binding for a private agent. Names must remain stable across activations.</summary>
public static class InternalAgentMemory
{
    public static IAgentMemoryService ForInternalAgent(this IAgentMemoryProvider provider,
        string coreScope, string name, InternalAgentMemoryMode mode = InternalAgentMemoryMode.OwnOnly,
        AgentMemoryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        coreScope = coreScope.Trim();
        if (coreScope.Length > 200) throw new ArgumentException("Memory scopes must fit the SQL 200-character scope key.", nameof(coreScope));
        if (mode == InternalAgentMemoryMode.CoreOnly) return provider.GetMemoryService(coreScope);
        // Length-prefixed components avoid ambiguous parent/name combinations.
        var ownScope = $"internal:{coreScope.Length}:{coreScope}:{name.Trim()}";
        if (ownScope.Length > 200) throw new ArgumentException("The combined parent and internal-agent name exceeds the SQL scope-key limit.", nameof(name));
        var own = provider.GetMemoryService(ownScope);
        return mode == InternalAgentMemoryMode.OwnOnly ? own
            : new LayeredMemoryService(own, provider.GetMemoryService(coreScope), options ?? new());
    }
}
