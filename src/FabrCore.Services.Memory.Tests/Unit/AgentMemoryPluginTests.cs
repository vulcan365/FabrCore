using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Plugin;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class AgentMemoryPluginTests
{
    [TestMethod]
    public async Task UpdateTool_InvokesBoundServiceWithTemperature()
    {
        var memory = Substitute.For<IAgentMemoryService>();
        var provider = Substitute.For<IAgentMemoryProvider>();
        provider.GetMemoryService("agent:one").Returns(memory);
        var id = Guid.NewGuid();
        memory.UpdateMemoryAsync(id, null, null, null, null, MemoryTemperature.Cold, Arg.Any<CancellationToken>())
            .Returns(new MemoryEntry { Id = id, Title = "Policy", Temperature = MemoryTemperature.Cold });
        using var services = new ServiceCollection().AddLogging().AddSingleton(provider).BuildServiceProvider();
        var plugin = new AgentMemoryPlugin();
        await plugin.InitializeAsync(new AgentConfiguration { Handle = "agent:one" }, services);
        var tool = AIFunctionFactory.Create(typeof(AgentMemoryPlugin).GetMethod(nameof(AgentMemoryPlugin.UpdateMemory))!, plugin);
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["memoryId"] = id.ToString(), ["temperature"] = "Cold" });
        StringAssert.Contains(result!.ToString()!, "Cold");
        await memory.Received(1).UpdateMemoryAsync(id, null, null, null, null, MemoryTemperature.Cold, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task InvalidToolArguments_ReturnErrorsBeforeCallingService()
    {
        var plugin = new AgentMemoryPlugin();
        StringAssert.StartsWith(await plugin.SaveMemory("title", "999", "content"), "Error:");
        StringAssert.StartsWith(await plugin.SearchArchive("query", typeFilter: "invalid"), "Error:");
        StringAssert.StartsWith(await plugin.UpdateMemory(Guid.NewGuid().ToString(), temperature: "999"), "Error:");
        StringAssert.StartsWith(await plugin.UpdateMemory("bad-id"), "Error:");
    }

    [TestMethod]
    public async Task SaveMemory_InvalidType_ListsCurrentTaxonomy()
    {
        var plugin = new AgentMemoryPlugin();

        var result = await plugin.SaveMemory("title", "Bogus", "content");

        StringAssert.StartsWith(result, "Error: Invalid memory type 'Bogus'");
        foreach (var type in Enum.GetNames<MemoryType>())
            StringAssert.Contains(result, type);
    }

    [TestMethod]
    public async Task ForgetMemory_InvalidGuid_ReturnsActionableError()
    {
        var result = await new AgentMemoryPlugin().ForgetMemory("not-a-guid");

        StringAssert.Contains(result, "Invalid memory ID format");
    }
}
