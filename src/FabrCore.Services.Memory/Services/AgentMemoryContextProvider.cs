using FabrCore.Services.Memory.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Services;

/// <summary>Per-invocation, bounded SQL memory recall for Agent Framework and FabrCore harnesses.</summary>
public sealed class AgentMemoryContextProvider : AIContextProvider
{
    private readonly IAgentMemoryService memory;
    private readonly int maxCharacters;

    public AgentMemoryContextProvider(IAgentMemoryService memory, int maxCharacters = 12000)
    {
        this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        if (maxCharacters < 512) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        this.maxCharacters = maxCharacters;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var query = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User && m.AuthorName != "agent-memory")?.Text;
        if (string.IsNullOrWhiteSpace(query)) return new AIContext();
        var recall = await memory.RecallAsync(query, ct: cancellationToken);
        // Put useful retrieved content before the pointer index within the context budget.
        recall.HotIndex = new();
        var text = memory.FormatRecallContext(recall);
        if (string.IsNullOrEmpty(text)) return new AIContext();
        const string suffix = "\n[Memory context truncated; use recall/search tools for more.]\n</memory-context>";
        if (text.Length > maxCharacters) text = text[..(maxCharacters - suffix.Length)] + suffix;
        return new AIContext { Messages = [new ChatMessage(ChatRole.User, text) { AuthorName = "agent-memory" }] };
    }
}
