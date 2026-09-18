using System.Text.Json;
using FabrCore.SampleApp.Scripting;
using FabrCore.SampleApp.Surface;
using FabrCore.Scripting;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FabrCore.SampleApp.Tests;

public sealed class ScriptingDemoTests
{
    [Fact]
    public void BlueprintProvisionsScriptingAgentWithItsPlugin()
    {
        var config = Assert.Single(SurfaceDemoBlueprintFactory.Create().Agents,
            agent => agent.Handle == SurfaceDemoBlueprintFactory.ScriptingAgentHandle);
        Assert.Equal(ScriptingDemoAgent.Alias, config.AgentType);
        Assert.Contains(SalesScriptingPlugin.Alias, config.Plugins);
        Assert.Equal("default", config.Models);
    }

    [Fact]
    [Trait("TestCategory", "Integration")]
    public async Task BlueprintPluginCalculatesSalesInWorkerAndReturnsReport()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fabrcore-sample-scripting", Guid.NewGuid().ToString("N")));
        try
        {
            await using var services = new ServiceCollection().AddLogging().AddFabrCoreScripting(options =>
            {
                options.CacheDirectory = Path.Combine(root, "cache");
                options.WorkingDirectory = Path.Combine(root, "runs");
            }).BuildServiceProvider();
            var config = Assert.Single(SurfaceDemoBlueprintFactory.Create().Agents,
                agent => agent.Handle == SurfaceDemoBlueprintFactory.ScriptingAgentHandle);
            var registry = new FabrCoreToolRegistry(NullLogger<FabrCoreToolRegistry>.Instance,
                [typeof(SalesScriptingPlugin).Assembly]);
            await using var scope = await registry.ResolveToolScopeAsync(services, config.Plugins, [], config);

            var environmentTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(scope.Tools, tool => tool.Name == "GetScriptingEnvironment"));
            var environment = JsonSerializer.SerializeToElement(await environmentTool.InvokeAsync(new AIFunctionArguments()));
            Assert.Contains("Newtonsoft.Json", environment.ToString());
            var dataTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(scope.Tools, tool => tool.Name == "GetDemoSales"));
            var data = JsonSerializer.SerializeToElement(await dataTool.InvokeAsync(new AIFunctionArguments()));
            var execute = Assert.IsAssignableFrom<AIFunction>(Assert.Single(scope.Tools, tool => tool.Name == "ExecuteCSharp"));
            var value = await execute.InvokeAsync(new AIFunctionArguments
            {
                ["code"] = SalesScriptingPlugin.SalesReportScript,
                ["input"] = data
            });
            var result = JsonSerializer.SerializeToElement(value).Deserialize<ScriptExecutionResult>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            Assert.True(result.Status == ScriptExecutionStatus.Success, result.Error);
            var report = result.Value!.Value.GetProperty("Report");
            Assert.Equal(3, report.GetProperty("OrderCount").GetInt32());
            Assert.Equal("USD", report.GetProperty("Currency").GetString());
            Assert.Equal(400m, report.GetProperty("Total").GetDecimal());
            Assert.Equal(250m, report.GetProperty("ByCategory").GetProperty("Bikes").GetDecimal());
            Assert.Equal(150m, report.GetProperty("ByCategory").GetProperty("Accessories").GetDecimal());
            Assert.NotEqual(Environment.ProcessId, result.Value.Value.GetProperty("WorkerProcessId").GetInt32());
            Assert.Contains("Calculated all 3 sample orders.", result.Output);

            var artifact = Assert.Single(result.Artifacts);
            Assert.Equal("sales-report.json", artifact.Name);
            using var artifactJson = JsonDocument.Parse(artifact.Content);
            Assert.Equal(400m, artifactJson.RootElement.GetProperty("Total").GetDecimal());
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "runs")));
        }
        finally
        {
            // Only remove this test's unique directory, after the plugin and services are disposed.
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fabrcore-sample-scripting")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected scripting test directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
