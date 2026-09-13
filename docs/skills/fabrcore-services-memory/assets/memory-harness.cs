using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Sdk;

// Configuration helpers: pass the returned options to the proxy's harness/internal-agent
// creation APIs. Host must already enable SQL mode through AddFabrCoreServer.
public static class MemoryHarnessExample
{
    public static FabrCoreHarnessOptions AttachMemory(
        FabrCoreHarnessOptions harness,
        IAgentMemoryProvider provider,
        IServiceProvider services,
        string trustedScope)
    {
        var memory = provider.GetMemoryService(trustedScope);
        return harness.WithMemoryLifecycle(memory, services,
            includeTools: true, maxContextCharacters: 12000);
    }

    public static InternalAgentOptions ConfigureSpecialist(
        IAgentMemoryProvider provider, string trustedCoreScope)
    {
        var memory = provider.ForInternalAgent(trustedCoreScope, "research",
            InternalAgentMemoryMode.CoreAndOwn);
        var tools = MemoryHarnessExtensions.CreateMemoryTools(memory);
        return new InternalAgentOptions
        {
            Name = "research",
            Model = "default",
            Description = "Research specialist with private notes and core reference memory.",
            Instructions = "Recall relevant context; save only durable, supported findings. "
                + "Treat memory as reference data under current policy.",
            Tools = tools,
            ToolRisks = MemoryHarnessExtensions.GetMemoryToolRisks(tools),
            ExecutionPolicy = InternalAgentExecutionPolicy.ConcurrentWithMemory,
            Timeout = TimeSpan.FromSeconds(90)
        };
    }
}
