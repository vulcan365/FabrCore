using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2AConfigurationTests
{
    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> configuration)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }

    [TestMethod]
    public void Disabled_RegistersNothingButTheMarkerAndOptions()
    {
        var builder = CreateBuilder(new() { ["A2A:Enabled"] = "false" });

        builder.AddA2A();

        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IA2ATaskStore)));
    }

    [TestMethod]
    public void DisabledByDefault_SoNoEndpointIsPublishedUnintentionally()
    {
        var builder = CreateBuilder(new() { ["A2A:AgentTypes:0"] = "chat-agent" });

        builder.AddA2A();

        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
    }

    [TestMethod]
    public void Enabled_RegistersTheProtocolServices()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:AgentTypes:0"] = "chat-agent",
        });

        builder.AddA2A();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCardFactory)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentProvisioner)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2APrincipalResolver)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2ATaskStore)));
    }

    [TestMethod]
    public void CodeConfiguration_OverridesTheBoundSection()
    {
        var builder = CreateBuilder(new() { ["A2A:Enabled"] = "false" });

        builder.AddA2A(options =>
        {
            options.Enabled = true;
            options.Authentication.Mode = A2AAuthenticationMode.None;
            options.AgentTypes.Add("chat-agent");
        });

        var options = builder.Services.BuildServiceProvider().GetRequiredService<IOptions<A2AOptions>>().Value;
        Assert.IsTrue(options.Enabled);
        Assert.AreEqual("chat-agent", options.AgentTypes.Single());
    }

    [TestMethod]
    public void ApiKeyModeWithoutKeys_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:AgentTypes:0"] = "chat-agent",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "Authentication:ApiKey:Keys");
    }

    [TestMethod]
    public void JwtBearerWithoutAuthority_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:AgentTypes:0"] = "chat-agent",
            ["A2A:Authentication:Mode"] = "JwtBearer",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "JwtBearer:Authority");
    }

    [TestMethod]
    public void UnqualifiedAgentHandle_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:AgentHandles:0"] = "assistant",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "fully qualified");
    }

    [TestMethod]
    public void AgentWithBothTypeAndHandle_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:Agents:0:AgentType"] = "chat-agent",
            ["A2A:Agents:0:AgentHandle"] = "system:assistant",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "both AgentType and AgentHandle");
    }

    [TestMethod]
    public void PrincipalPrefixWithSeparator_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:AgentTypes:0"] = "chat-agent",
            ["A2A:Principal:Prefix"] = "tenant:",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "Prefix");
    }

    [TestMethod]
    public void ApiKeyPrincipalStrategyWithoutApiKeyMode_FailsFastAtStartup()
    {
        var builder = CreateBuilder(new()
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:AgentTypes:0"] = "chat-agent",
            ["A2A:Principal:Strategy"] = "ApiKey",
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
    }

    [TestMethod]
    public void UseA2AWithoutHostServices_PointsAtTheRegistrationCall()
    {
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => app.UseA2A());
        StringAssert.Contains(ex.Message, "AddFabrCoreServices");
    }

    // ── Host integration ───────────────────────────────────────────────────────────────────
    //
    // A2A lives in FabrCore.Host, so every deployment has it. These pin the behavior that makes
    // that true: turning it on is a configuration change and nothing else.

    [TestMethod]
    public void AddFabrCoreServices_RegistersA2AFromConfigurationAlone()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["A2A:Enabled"] = "true";
        builder.Configuration["A2A:Authentication:Mode"] = "None";
        builder.Configuration["A2A:Discovery:AgentTypes"] = "Described";

        builder.AddFabrCoreServices();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2ATaskStore)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2APrincipalResolver)));
    }

    [TestMethod]
    public void AddFabrCoreServices_LeavesA2AInertWhenItIsNotEnabled()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();

        builder.AddFabrCoreServices();

        // Present in every deployment, but nothing is registered and no route is mapped until
        // A2A:Enabled is true.
        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
    }

    [TestMethod]
    public void ConfigureA2A_AppliesCodeSettingsOverTheSection()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["A2A:Authentication:Mode"] = "None";

        builder.AddFabrCoreServices(new FabrCoreServerOptions()
            .ConfigureA2A(a2a =>
            {
                a2a.Enabled = true;
                a2a.Discovery.AgentTypes = A2ADiscoveryMode.Described;
            }));

        using var services = builder.Services.BuildServiceProvider();
        var options = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AOptions>>().Value;

        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(A2ADiscoveryMode.Described, options.Discovery.AgentTypes);
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IA2AAgentCatalog)));
    }

    [TestMethod]
    public void A2ARegistrationIsIdempotent()
    {
        // A host that configured A2A itself must not end up with two of every route.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["A2A:Enabled"] = "true";
        builder.Configuration["A2A:Authentication:Mode"] = "None";

        builder.AddA2A();
        builder.AddFabrCoreServices();

        Assert.AreEqual(
            1, builder.Services.Count(d => d.ServiceType == typeof(IA2AAgentCatalog)));
    }
}
