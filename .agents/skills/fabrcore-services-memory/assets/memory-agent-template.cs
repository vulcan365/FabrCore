using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// Agent with structured memory and three-tier memory-aware compaction.
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private IAgentMemoryService? _memory;
    private MemoryCompactionHandler? _compactionHandler;
    private AIAgent? _agent;
    private AgentSession? _session;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Set up memory service and compaction handler
        var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
        // Resolves the memory scope: shared scope when configured (plugin setting or
        // Args["MemoryScope"]), otherwise the agent handle (isolated memory).
        _memory = provider.GetMemoryService(MemoryScopeResolver.Resolve(config));

        var compactionService = serviceProvider.GetRequiredService<MemoryAwareCompactionService>();
        var memoryOptions = serviceProvider.GetRequiredService<AgentMemoryOptions>();
        _compactionHandler = new MemoryCompactionHandler(
            _memory, compactionService, memoryOptions,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        // 2. Inject hot layer index into system prompt
        var index = await _memory.GetMemoryIndexAsync();
        if (index.Entries.Count > 0)
        {
            var memoryBlock = string.Join("\n", index.Entries.Select(e =>
                $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));
            config.SystemPrompt += $"\n\n## Agent Memory\n{memoryBlock}";
        }

        // 3. Set up tools and LLM agent
        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        SetStatusMessage("Recalling memories...");

        // Recall relevant warm memories for this query
        var recall = await _memory!.RecallAsync(message.Message);

        // Format with memory-context markers (prevents re-extraction during compaction)
        var memoryContext = _memory.FormatRecallContext(recall);

        SetStatusMessage(null);
        var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }

        return response;
    }

    public override async Task<CompactionResult?> OnCompaction(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        CompactionConfig compactionConfig,
        int estimatedTokens = 0)
    {
        // Three-tier cascade: tool compression → memory extraction → structured summary
        return await _compactionHandler!.CompactAsync(chatHistoryProvider, compactionConfig);
    }
}
