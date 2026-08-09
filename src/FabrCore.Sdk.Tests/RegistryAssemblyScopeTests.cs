using FabrCore.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class RegistryAssemblyScopeTests
{
    [TestMethod]
    public void Explicit_registry_scope_excludes_types_from_other_loaded_assemblies()
    {
        var included = new FabrCoreRegistry(
            NullLogger<FabrCoreRegistry>.Instance,
            [typeof(ScopedRegistryAgent).Assembly]);
        var excluded = new FabrCoreRegistry(
            NullLogger<FabrCoreRegistry>.Instance,
            [typeof(string).Assembly]);

        Assert.IsTrue(included.GetAgentTypes()
            .SelectMany(entry => entry.Aliases)
            .Contains(ScopedRegistryAgent.Alias, StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(excluded.GetAgentTypes()
            .SelectMany(entry => entry.Aliases)
            .Contains(ScopedRegistryAgent.Alias, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Explicit_tool_registry_scope_controls_plugin_and_tool_resolution()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var included = new FabrCoreToolRegistry(
            NullLogger<FabrCoreToolRegistry>.Instance,
            [typeof(ScopedRegistryAgent).Assembly]);
        var excluded = new FabrCoreToolRegistry(
            NullLogger<FabrCoreToolRegistry>.Instance,
            [typeof(string).Assembly]);

        var includedTools = await included.ResolveToolsAsync(
            services,
            [ScopedRegistryPlugin.Alias],
            [ScopedRegistryTools.Alias],
            new AgentConfiguration());
        var excludedTools = await excluded.ResolveToolsAsync(
            services,
            [ScopedRegistryPlugin.Alias],
            [ScopedRegistryTools.Alias],
            new AgentConfiguration());

        Assert.HasCount(2, includedTools);
        Assert.IsEmpty(excludedTools);
    }

    [AgentAlias(Alias)]
    public sealed class ScopedRegistryAgent
    {
        public const string Alias = "scoped-registry-test-agent";
    }

    [PluginAlias(Alias)]
    public sealed class ScopedRegistryPlugin : IFabrCorePlugin
    {
        public const string Alias = "scoped-registry-test-plugin";

        public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider) =>
            Task.CompletedTask;

        [System.ComponentModel.Description("Returns a scoped registry test result.")]
        public string Execute() => "plugin";
    }

    public static class ScopedRegistryTools
    {
        public const string Alias = "scoped-registry-test-tool";

        [ToolAlias(Alias)]
        [System.ComponentModel.Description("Returns a standalone scoped registry test result.")]
        public static string Execute() => "tool";
    }
}
