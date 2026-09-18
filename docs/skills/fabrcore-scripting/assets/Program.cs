using System.Text.Json;
using FabrCore.Core;
using FabrCore.Scripting;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

await using var services = new ServiceCollection()
    .AddLogging()
    .AddFabrCoreScripting()
    .BuildServiceProvider();

var registry = new FabrCoreToolRegistry(services.GetRequiredService<ILogger<FabrCoreToolRegistry>>(),
    [typeof(ReportingScriptsPlugin).Assembly]);

// Agents can select this same alias in config.Plugins before ResolveConfiguredToolsAsync().
// This sample invokes the resulting tool directly, so it needs no model/API credentials.
var config = new AgentConfiguration { Handle = "sample:reporting", Plugins = ["reporting-scripts"] };
await using var scope = await registry.ResolveToolScopeAsync(services, config.Plugins, [], config);
var execute = (AIFunction)scope.Tools.Single(t => t.Name == "ExecuteCSharp");
var result = await execute.InvokeAsync(new AIFunctionArguments
{
    ["code"] = """
        var input = JObject.Parse(InputJson);
        var total = input["orders"]!.Values<decimal>().Sum();
        Console.WriteLine("Calculated in worker " + System.Environment.ProcessId);
        return new { Customer = input["customer"]!.ToString(), Total = total };
        """,
    ["input"] = JsonSerializer.SerializeToElement(new { customer = "Eric", orders = new[] { 125, 75, 200 } })
});
Console.WriteLine("Agent host process: " + Environment.ProcessId);
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
