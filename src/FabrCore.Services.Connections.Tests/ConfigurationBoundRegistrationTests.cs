using FabrCore.Connections;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.RemoteAgents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.Connections.Tests;

[TestClass]
public sealed class ConfigurationBoundRegistrationTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static T? Instance<T>(IServiceCollection services) where T : class =>
        services.LastOrDefault(d => d.ServiceType == typeof(T))?.ImplementationInstance as T;

    // ----- Connections -----

    [TestMethod]
    public void Connections_BindFromTheFabrCoreSection()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreConnections(Configuration(new()
        {
            ["FabrCore:Connections:Enabled"] = "true",
            ["FabrCore:Connections:EntraAgentIdEnabled"] = "true",
            ["FabrCore:Connections:ClientHandoffEnabled"] = "true",
            ["FabrCore:Connections:HandoffAuthority"] = "https://login.microsoftonline.com/tenant/v2.0",
            ["FabrCore:Connections:HandoffAudience"] = "api://fabrcore",
        }));

        var options = Instance<ConnectionsOptions>(services);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.Enabled);
        Assert.IsTrue(options.EntraAgentIdEnabled);
        Assert.AreEqual("api://fabrcore", options.HandoffAudience);
        var capabilities = Instance<ConnectionCapabilities>(services);
        Assert.IsNotNull(capabilities);
        Assert.IsTrue(capabilities.EntraAgentIdEnabled);
        Assert.IsTrue(capabilities.ClientHandoffEnabled);
    }

    [TestMethod]
    public void Connections_StayUnregistered_WithoutTheSection()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreConnections(Configuration(new()));

        Assert.AreEqual(0, services.Count, "A disabled feature must add no services.");
    }

    [TestMethod]
    public void Connections_IgnoreTheAgentsSdkConnectionsSection()
    {
        // The Microsoft 365 Agents SDK owns top-level "Connections". Reading it here would let an
        // unrelated bot setting switch this feature on.
        var services = new ServiceCollection();

        services.AddFabrCoreConnections(Configuration(new()
        {
            ["Connections:Enabled"] = "true",
            ["Connections:ServiceConnection:Settings:ClientId"] = "bot",
        }));

        Assert.AreEqual(0, services.Count);
    }

    [TestMethod]
    public void Connections_CodeRunsLast_SoCodeWins()
    {
        var enabledInCode = new ServiceCollection();
        enabledInCode.AddFabrCoreConnections(Configuration(new() { ["FabrCore:Connections:Enabled"] = "false" }), o => o.Enabled = true);
        Assert.IsTrue(Instance<ConnectionsOptions>(enabledInCode)!.Enabled);

        var disabledInCode = new ServiceCollection();
        disabledInCode.AddFabrCoreConnections(Configuration(new() { ["FabrCore:Connections:Enabled"] = "true" }), o => o.Enabled = false);
        Assert.AreEqual(0, disabledInCode.Count);

        var pinned = new ServiceCollection();
        pinned.AddFabrCoreConnections(
            Configuration(new() { ["FabrCore:Connections:Enabled"] = "true", ["FabrCore:Connections:EntraAgentIdEnabled"] = "true" }),
            o => o.EntraAgentIdEnabled = false);
        Assert.IsFalse(Instance<ConnectionsOptions>(pinned)!.EntraAgentIdEnabled);
    }

    [TestMethod]
    public void Connections_RejectAValueThatIsNotABoolean()
    {
        var services = new ServiceCollection();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddFabrCoreConnections(Configuration(new() { ["FabrCore:Connections:Enabled"] = "yes please" })));
    }

    [TestMethod]
    public void Connections_TheDelegateOnlyOverload_IsUnchanged()
    {
        var services = new ServiceCollection();
        services.AddFabrCoreConnections(o => o.Enabled = false);
        Assert.AreEqual(0, services.Count);

        services.AddFabrCoreConnections(o => o.Enabled = true);
        Assert.IsTrue(Instance<ConnectionsOptions>(services)!.Enabled);
    }

    // ----- Remote agents -----

    [TestMethod]
    public void RemoteAgents_ReadEnabledAndTimeoutFromConfiguration()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreRemoteAgents(Configuration(new()
        {
            ["FabrCore:RemoteAgents:Enabled"] = "true",
            ["FabrCore:RemoteAgents:Timeout"] = "00:00:45",
        }));

        var options = Instance<RemoteAgentOptions>(services);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(TimeSpan.FromSeconds(45), options.Timeout);
    }

    [TestMethod]
    public void RemoteAgents_AdvertiseThemselvesEvenWhenOff()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreRemoteAgents(Configuration(new()));

        Assert.IsNull(Instance<RemoteAgentOptions>(services), "The agent reads these options and must see none while off.");
        var contributor = Instance<IFabrCoreCapabilityContributor>(services);
        Assert.IsNotNull(contributor);
        var service = contributor.GetServices().Single();
        Assert.AreEqual("remote-agents", service.Name);
        Assert.IsFalse(service.Available);
        Assert.AreEqual("FabrCore:RemoteAgents:Enabled is false.", service.UnavailableReason);
        CollectionAssert.AreEqual(new[] { "work-iq", "copilot-studio" }, service.Features);
        Assert.AreEqual("1", contributor.GetHeartbeatCapabilities()["remote-agents"]);
        Assert.AreEqual("false", contributor.GetHeartbeatCapabilities()["remote-agents.enabled"]);
    }

    [TestMethod]
    public void RemoteAgents_AdvertiseAsAvailableWhenOn()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreRemoteAgents(o => o.Enabled = true);

        var contributor = Instance<IFabrCoreCapabilityContributor>(services)!;
        var service = contributor.GetServices().Single();
        Assert.IsTrue(service.Available);
        Assert.IsNull(service.UnavailableReason);
        Assert.AreEqual("true", contributor.GetHeartbeatCapabilities()["remote-agents.enabled"]);
    }

    [TestMethod]
    public void RemoteAgents_CodeRunsLast_SoCodeWins()
    {
        var services = new ServiceCollection();

        services.AddFabrCoreRemoteAgents(
            Configuration(new() { ["FabrCore:RemoteAgents:Enabled"] = "false", ["FabrCore:RemoteAgents:Timeout"] = "00:01:00" }),
            o => { o.Enabled = true; o.Timeout = TimeSpan.FromSeconds(30); });

        var options = Instance<RemoteAgentOptions>(services)!;
        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [TestMethod]
    [DataRow("FabrCore:RemoteAgents:Enabled", "enabled")]
    [DataRow("FabrCore:RemoteAgents:Timeout", "two minutes")]
    public void RemoteAgents_RejectUnreadableValues_NamingTheKey(string key, string value)
    {
        var services = new ServiceCollection();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddFabrCoreRemoteAgents(Configuration(new() { [key] = value })));

        StringAssert.Contains(error.Message, key);
    }

    [TestMethod]
    [DataRow("00:00:00")]
    [DataRow("00:10:01")]
    public void RemoteAgents_RejectATimeoutOutsideItsRange(string timeout)
    {
        var services = new ServiceCollection();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => services.AddFabrCoreRemoteAgents(Configuration(new()
        {
            ["FabrCore:RemoteAgents:Enabled"] = "true",
            ["FabrCore:RemoteAgents:Timeout"] = timeout,
        })));
    }
}
