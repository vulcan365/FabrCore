using FabrCore.Core;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Services.Contracts.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class ClusterCapabilityFactoryTests
{
    private static ClusterCapabilityDocument Create(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return ClusterCapabilityFactory.Create(
            new FabrCoreFeatureState(false), [], new RemoteAdministrationOptions(), services.BuildServiceProvider());
    }

    [TestMethod]
    public void A2A_IsAlwaysListed_AndUnavailableUntilEnabled()
    {
        var a2a = Create().Services.Single(s => s.Name == "a2a");

        Assert.IsFalse(a2a.Available);
        Assert.AreEqual("A2A:Enabled is false.", a2a.UnavailableReason);
        Assert.AreEqual("1.0", a2a.ApiVersion);
        CollectionAssert.AreEqual(
            new[] { "status", "jsonrpc", "http-json", "streaming", "tasks", "auth-apikey", "principal-fixed", "agent-bindings" },
            a2a.Features);
    }

    [TestMethod]
    public void A2A_ReportsTheAuthenticationModeAndPrincipalStrategyInUse()
    {
        var options = new A2AOptions { Enabled = true };
        options.Authentication.Mode = A2AAuthenticationMode.JwtBearer;
        options.Principal.Strategy = A2APrincipalStrategy.CanonicalEntra;

        var a2a = Create(s => s.AddSingleton(Microsoft.Extensions.Options.Options.Create(options)))
            .Services.Single(s => s.Name == "a2a");

        Assert.IsTrue(a2a.Available);
        Assert.IsNull(a2a.UnavailableReason);
        CollectionAssert.Contains(a2a.Features, "auth-jwtbearer");
        CollectionAssert.Contains(a2a.Features, "principal-canonicalentra");
    }

    [TestMethod]
    public void A2A_IsListedWithoutAServiceProvider()
    {
        var document = ClusterCapabilityFactory.Create(new FabrCoreFeatureState(false), [], new RemoteAdministrationOptions());

        Assert.IsFalse(document.Services.Single(s => s.Name == "a2a").Available);
    }

    [TestMethod]
    public void ContributedServices_AreAdded_IncludingDisabledOnes()
    {
        var document = Create(s => s.AddSingleton<IFabrCoreCapabilityContributor>(new Contributor(
            new ClusterServiceCapability { Name = "addon", ApiVersion = "1", Features = ["status"], Available = false, UnavailableReason = "off" })));

        var addon = document.Services.Single(s => s.Name == "addon");
        Assert.IsFalse(addon.Available);
        Assert.AreEqual("off", addon.UnavailableReason);
    }

    [TestMethod]
    public void ContributedServices_CannotReplaceAHostServiceOrAnEarlierContributor()
    {
        var document = Create(s =>
        {
            s.AddSingleton<IFabrCoreCapabilityContributor>(new Contributor(
                new() { Name = "HOST-ADMIN", Features = ["spoofed"] },
                new() { Name = "a2a", Features = ["spoofed"] },
                new() { Name = "addon", Features = ["first"] },
                new() { Name = "  ", Features = ["blank"] }));
            s.AddSingleton<IFabrCoreCapabilityContributor>(new Contributor(new ClusterServiceCapability { Name = "Addon", Features = ["second"] }));
        });

        Assert.IsFalse(document.Services.Any(s => s.Features.Contains("spoofed")));
        Assert.IsFalse(document.Services.Any(s => s.Features.Contains("blank")));
        CollectionAssert.AreEqual(new[] { "first" }, document.Services.Single(s => s.Name.Equals("addon", StringComparison.OrdinalIgnoreCase)).Features);
        Assert.AreEqual(1, document.Services.Count(s => s.Name.Equals("host-admin", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AContributorThatThrows_IsSkipped_WithoutLosingTheDocument()
    {
        var document = Create(s =>
        {
            s.AddSingleton<IFabrCoreCapabilityContributor>(new Contributor(null!));
            s.AddSingleton<IFabrCoreCapabilityContributor>(new Contributor(new ClusterServiceCapability { Name = "after" }));
        });

        Assert.IsTrue(document.Services.Any(s => s.Name == "host-admin"));
        Assert.IsTrue(document.Services.Any(s => s.Name == "after"));
    }

    [TestMethod]
    public void HeartbeatCapabilities_DefaultToEmpty()
    {
        IFabrCoreCapabilityContributor contributor = new ServicesOnlyContributor();

        Assert.AreEqual(0, contributor.GetHeartbeatCapabilities().Count);
    }

    private sealed class Contributor(params ClusterServiceCapability[] services) : IFabrCoreCapabilityContributor
    {
        public IEnumerable<ClusterServiceCapability> GetServices() =>
            services ?? throw new InvalidOperationException("contributor failure");
    }

    private sealed class ServicesOnlyContributor : IFabrCoreCapabilityContributor
    {
        public IEnumerable<ClusterServiceCapability> GetServices() => [];
    }
}
