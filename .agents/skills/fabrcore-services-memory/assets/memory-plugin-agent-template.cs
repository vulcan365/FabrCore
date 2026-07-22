using FabrCore.Core;
using FabrCore.Services.Memory.Plugin;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// Agent that exposes memory as LLM-callable tools, allowing the model to autonomously
/// save, recall, and manage memories.
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Initialize the memory plugin. Scope resolves automatically: shared scope
        //    when configured (plugin setting "MemoryScope"), else the agent handle.
        var memoryPlugin = new AgentMemoryPlugin();
        await memoryPlugin.InitializeAsync(config, serviceProvider);

        // 2. Resolve configured tools and add memory tools
        var tools = await ResolveConfiguredToolsAsync();
        var pluginType = typeof(AgentMemoryPlugin);
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.SaveMemory))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.RecallMemories))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.SearchArchive))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.ForgetMemory))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.GetMemoryIndex))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.ConsolidateMemories))!, memoryPlugin));

        // 3. Set system prompt with memory guidance
        if (string.IsNullOrWhiteSpace(config.SystemPrompt))
        {
            config.SystemPrompt = """
                You are an agent with persistent memory. Use your memory tools to:
                - recall_memories: Check memories before answering questions about past interactions
                - save_memory: Store important user preferences, feedback, and project context
                - forget_memory: Remove outdated or incorrect memories
                - consolidate_memories: Clean up duplicates when memory quality degrades

                Memory types:
                - Fact: verified truths, domain knowledge, system behaviors
                - Rule: business rules, constraints, policies, conventions
                - Instruction: user directives, preferences, standing orders
                - Observation: patterns noticed, inferences, situational context

                Save only durable knowledge that will still be true and useful in future conversations.
                Prefer fewer high-confidence memories over many speculative ones.
                """;
        }

        // 4. Create the chat client agent
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
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }
}
