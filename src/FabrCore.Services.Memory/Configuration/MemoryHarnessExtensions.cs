using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Plugin;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Configuration;

public static class MemoryHarnessExtensions
{
    /// <summary>Configure recall, optional tools, and memory-aware persisted-history compaction
    /// together for a proxy-created FabrCore harness.</summary>
    public static FabrCoreHarnessOptions WithMemoryLifecycle(this FabrCoreHarnessOptions options,
        IAgentMemoryService memory, IServiceProvider services, bool includeTools = false, int maxContextCharacters = 12000)
    {
        ArgumentNullException.ThrowIfNull(services);
        var handler = new MemoryCompactionHandler(memory,
            services.GetRequiredService<MemoryAwareCompactionService>(),
            services.GetRequiredService<AgentMemoryOptions>(), services.GetRequiredService<ILoggerFactory>());
        return options.WithMemory(memory, includeTools, maxContextCharacters).WithMemoryCompaction(handler);
    }

    /// <summary>Register memory-aware history compaction on the proxy-created harness.
    /// Failures propagate to the proxy's existing failure reporting; history is preserved.</summary>
    public static FabrCoreHarnessOptions WithMemoryCompaction(this FabrCoreHarnessOptions options,
        MemoryCompactionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        options.HistoryCompaction = (history, config) => handler.CompactAsync(history, config);
        return options;
    }

    /// <summary>Add scoped recall to the existing harness provider pipeline, preserving compaction.
    /// Tools are opt-in. Use WithMemoryLifecycle to also register history extraction.</summary>
    public static FabrCoreHarnessOptions WithMemory(this FabrCoreHarnessOptions options,
        IAgentMemoryService memory, bool includeTools = false, int maxContextCharacters = 12000)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = new AgentMemoryContextProvider(memory, maxContextCharacters);
        if (includeTools)
        {
            var tools = (options.ChatOptions?.Tools ?? []).Concat(CreateMemoryTools(memory)).ToList();
            var duplicate = tools.OfType<AIFunction>().GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) throw new ArgumentException($"Duplicate harness tool '{duplicate.Key}'. Configure memory tools through one entry point.", nameof(options));
            options.ChatOptions ??= new ChatOptions();
            options.ChatOptions.Tools = tools;
        }
        options.AIContextProviders = (options.AIContextProviders ?? []).Prepend(provider).ToArray();
        return options;
    }

    /// <summary>Create an explicit tool set. Read-only is suitable for internal background agents;
    /// mutating tools must follow the host's internal-agent execution and approval policies.</summary>
    public static IList<AITool> CreateMemoryTools(IAgentMemoryService memory, bool readOnly = false)
    {
        var plugin = new AgentMemoryPlugin(memory);
        string[] names = readOnly
            ? [nameof(AgentMemoryPlugin.RecallMemories), nameof(AgentMemoryPlugin.SearchArchive), nameof(AgentMemoryPlugin.GetMemoryIndex)]
            : [nameof(AgentMemoryPlugin.SaveMemory), nameof(AgentMemoryPlugin.SaveProcedure), nameof(AgentMemoryPlugin.UpdateMemory),
                nameof(AgentMemoryPlugin.ForgetMemory), nameof(AgentMemoryPlugin.RecallMemories), nameof(AgentMemoryPlugin.SearchArchive),
                nameof(AgentMemoryPlugin.GetMemoryIndex), nameof(AgentMemoryPlugin.ConsolidateMemories)];
        return names.Select(name => {
            var function = AIFunctionFactory.Create(typeof(AgentMemoryPlugin).GetMethod(name)!, plugin);
            var isRead = name is nameof(AgentMemoryPlugin.RecallMemories) or nameof(AgentMemoryPlugin.SearchArchive) or nameof(AgentMemoryPlugin.GetMemoryIndex);
            return (AITool)new ScopedMemoryFunction(function, memory.ScopeKey, isRead);
        }).ToList();
    }

    /// <summary>Classifies the exact memory tools returned by CreateMemoryTools for a specialist.</summary>
    public static IReadOnlyDictionary<string, InternalAgentToolRisk> GetMemoryToolRisks(IList<AITool> tools)
        => tools.Select(t => t as ScopedMemoryFunction ?? throw new ArgumentException("Only tools from CreateMemoryTools may be classified here.", nameof(tools)))
            .ToDictionary(t => t.Name, t => t.ReadOnly ? InternalAgentToolRisk.Read : InternalAgentToolRisk.MemoryWrite);
}
