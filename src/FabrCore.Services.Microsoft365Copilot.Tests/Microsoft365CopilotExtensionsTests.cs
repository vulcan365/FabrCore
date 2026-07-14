using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FabrCore.Core;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class Microsoft365CopilotExtensionsTests
{
    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> configuration)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }

    [TestMethod]
    public void RegistersBridgeAndAdapter()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:ClientId"] = "11111111-2222-3333-4444-555555555555",
            ["Microsoft365Copilot:ClientSecret"] = "secret",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
        });

        builder.AddMicrosoft365Copilot();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(FabrCoreCopilotAgent)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(ICopilotPrincipalResolver)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(ICopilotAgentProvisioner)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(CopilotAppPackageBuilder)));

        // Agents SDK configuration was synthesized from the Microsoft365Copilot section.
        Assert.AreEqual("11111111-2222-3333-4444-555555555555",
            builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void DisabledAddon_RegistersOnlyMarkerAndOptions()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:Enabled"] = "false",
        });

        builder.AddMicrosoft365Copilot();

        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
        Assert.IsNull(builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void Throws_WhenNoAgentConfigured()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:ClientId"] = "client",
            ["Microsoft365Copilot:ClientSecret"] = "secret",
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddMicrosoft365Copilot());
    }

    [TestMethod]
    public void Throws_WhenTokenValidationEnabled_WithoutClientId()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddMicrosoft365Copilot());
    }

    [TestMethod]
    public void AllowsMissingCredentials_ForLocalDevelopment()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
        });

        builder.AddMicrosoft365Copilot();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
    }

    [TestMethod]
    public void CodeOverride_WinsOverConfiguration()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
            ["Microsoft365Copilot:Agent:Handle"] = "from-config",
        });

        builder.AddMicrosoft365Copilot(o => o.Agent.Handle = "from-code");

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value;
        Assert.AreEqual("from-code", options.Agent.Handle);
    }

    [TestMethod]
    public void SharedAgentHandle_MustBeFullyQualified()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:SharedAgentHandle"] = "not-qualified",
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddMicrosoft365Copilot());
    }

    [TestMethod]
    public void ProactiveRelay_IsOptInAndUsesGenericRelayContract()
    {
        var disabled = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent"
        });
        disabled.AddMicrosoft365Copilot();
        Assert.IsFalse(disabled.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IPrincipalMessageRelay)));

        var enabled = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
            ["Microsoft365Copilot:Proactive:Enabled"] = "true"
        });
        enabled.AddMicrosoft365Copilot();

        Assert.IsTrue(enabled.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IPrincipalMessageRelay) &&
            descriptor.ImplementationType == typeof(CopilotPrincipalMessageRelay)));
    }
}
