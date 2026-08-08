using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class RegistryRegistrationTests
{
    [TestMethod]
    public async Task Default_registration_discovers_application_agents_and_tools_without_assembly_options()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RegistryRegistrationTests).Assembly.GetName().Name
        });

        builder.AddFabrCoreServices();

        await using var services = builder.Services.BuildServiceProvider();
        var registry = services.GetRequiredService<IFabrCoreRegistry>();
        var toolRegistry = services.GetRequiredService<FabrCoreToolRegistry>();

        Assert.AreEqual(typeof(DefaultRegistrationAgent), registry.FindAgentType(DefaultRegistrationAgent.Alias));

        var tools = await toolRegistry.ResolveToolsAsync(
            services,
            [DefaultRegistrationPlugin.Alias],
            null,
            new AgentConfiguration());

        Assert.HasCount(1, tools);
    }

    [TestMethod]
    public async Task Explicit_empty_registry_scope_remains_empty()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RegistryRegistrationTests).Assembly.GetName().Name
        });

        builder.AddFabrCoreServices(new FabrCoreServerOptions
        {
            RegistryAssemblies = []
        });

        await using var services = builder.Services.BuildServiceProvider();
        var registry = services.GetRequiredService<IFabrCoreRegistry>();
        var toolRegistry = services.GetRequiredService<FabrCoreToolRegistry>();

        Assert.IsNull(registry.FindAgentType(DefaultRegistrationAgent.Alias));

        var tools = await toolRegistry.ResolveToolsAsync(
            services,
            [DefaultRegistrationPlugin.Alias],
            null,
            new AgentConfiguration());

        Assert.IsEmpty(tools);
    }

    [TestMethod]
    public void Application_project_dependencies_are_loaded_automatically()
    {
        var assemblies = FabrCoreHostExtensions.LoadApplicationAssemblies(
            typeof(RegistryRegistrationTests).Assembly.GetName().Name,
            []);

        Assert.IsTrue(assemblies.Any(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "FabrCore.Host.SqlServer",
                StringComparison.OrdinalIgnoreCase)));
    }

    [AgentAlias(Alias)]
    public sealed class DefaultRegistrationAgent
    {
        public const string Alias = "host-default-registration-agent";
    }

    [PluginAlias(Alias)]
    public sealed class DefaultRegistrationPlugin : IFabrCorePlugin
    {
        public const string Alias = "host-default-registration-plugin";

        public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider) =>
            Task.CompletedTask;

        [System.ComponentModel.Description("Returns the default registry test result.")]
        public string Execute() => "registered";
    }
}
