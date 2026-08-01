using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Plugin;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class AgentMemoryPluginTests
{
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
