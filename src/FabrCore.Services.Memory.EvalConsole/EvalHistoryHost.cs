using System.Reflection;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.EvalConsole;

/// <summary>Minimal in-process history transport for exercising the production compaction service.
/// Memory itself uses real SQL; this deliberately does not claim Orleans crash-recovery coverage.</summary>
public class EvalHistoryHost : DispatchProxy
{
    public List<StoredChatMessage> Messages { get; private set; } = [];
    public static (IFabrCoreAgentHost Host, EvalHistoryHost State) From(IEnumerable<string> source)
    {
        var host = Create<IFabrCoreAgentHost, EvalHistoryHost>();
        var state = (EvalHistoryHost)(object)host;
        state.Messages = source.Select(text => new StoredChatMessage {
            Role = "user", Timestamp = DateTime.UtcNow,
            ContentsJson = JsonSerializer.Serialize<List<AIContent>>([new TextContent(text)], AgentAbstractionsJsonUtilities.DefaultOptions)
        }).ToList();
        return (host, state);
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IFabrCoreAgentHost.GetThreadMessagesAsync)) return Task.FromResult(Messages.ToList());
        if (targetMethod?.Name == nameof(IFabrCoreAgentHost.ReplaceThreadMessagesAsync))
        {
            Messages = ((IEnumerable<StoredChatMessage>)args![1]!).ToList();
            return Task.CompletedTask;
        }
        throw new NotSupportedException($"Eval history transport does not implement {targetMethod?.Name}.");
    }
}
