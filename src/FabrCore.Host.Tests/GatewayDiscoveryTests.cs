using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FabrCore.Core.Connectivity;
using FabrCore.Host.Api.Controllers;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class GatewayDiscoveryTests
{
    [TestMethod]
    public async Task Endpoint_IsDisabledByDefault()
    {
        var controller = CreateController(new GatewayDiscoveryOptions());

        var result = await controller.GetGateways();

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public async Task Endpoint_AnonymousCallerReceives401()
    {
        var controller = CreateController(EnabledOptions(), authenticated: false);

        var result = await controller.GetGateways();

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task Endpoint_UnauthorizedPolicyReceives403()
    {
        var controller = CreateController(EnabledOptions(), authorizationSucceeded: false);

        var result = await controller.GetGateways();

        var status = Assert.IsInstanceOfType<StatusCodeResult>(result);
        Assert.AreEqual(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [TestMethod]
    public async Task Endpoint_ReturnsClusterIdentityAndGatewaysWithoutProviderConfiguration()
    {
        var controller = CreateController(EnabledOptions());

        var result = await controller.GetGateways();

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        var document = Assert.IsInstanceOfType<FabrCoreGatewayDiscoveryDocument>(ok.Value);
        Assert.AreEqual(FabrCoreGatewayDiscoveryDocument.CurrentVersion, document.Version);
        Assert.AreEqual("cluster-a", document.ClusterId);
        Assert.AreEqual("service-a", document.ServiceId);
        CollectionAssert.AreEqual(
            new[] { "gwy.tcp://10.0.0.1:30000/0" },
            document.Gateways);

        var json = JsonSerializer.Serialize(document);
        Assert.IsFalse(json.Contains("ClusteringMode", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("ConnectionString", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("SqlServer", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("AzureStorage", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Endpoint_NoUsableGatewaysReceives503()
    {
        var controller = CreateController(EnabledOptions(), gateways: []);

        var result = await controller.GetGateways();

        var problem = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [TestMethod]
    public void ExplicitAdvertisedGatewaysTakePrecedence()
    {
        var members = new[]
        {
            Member("10.0.0.1", SiloStatus.Active),
            Member("10.0.0.2", SiloStatus.Active)
        };

        var gateways = GatewayDiscoverySource.SelectGateways(
            ["gwy.tcp://public.example:443/0"],
            members,
            30000);

        Assert.AreEqual("public.example", gateways.Single().Host);
        Assert.AreEqual(443, gateways.Single().Port);
    }

    [TestMethod]
    public void DerivedGatewaysIncludeOnlyActiveSilos()
    {
        var members = new[]
        {
            Member("10.0.0.1", SiloStatus.Active),
            Member("10.0.0.2", SiloStatus.Dead),
            Member("10.0.0.3", SiloStatus.Stopping)
        };

        var gateways = GatewayDiscoverySource.DeriveActiveGateways(members, 30000);

        Assert.AreEqual(1, gateways.Count);
        Assert.AreEqual("10.0.0.1", gateways[0].Host);
    }

    [TestMethod]
    public void DerivedGatewaysExcludeWildcardListeningAddresses()
    {
        var gateways = GatewayDiscoverySource.DeriveActiveGateways(
            [Member("0.0.0.0", SiloStatus.Active)],
            30000);

        Assert.AreEqual(0, gateways.Count);
    }

    [TestMethod]
    public void OptionsValidator_RequiresPolicyWhenEnabled()
    {
        var result = new GatewayDiscoveryOptionsValidator().Validate(
            null,
            new GatewayDiscoveryOptions { Enabled = true });

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "AuthorizationPolicy");
    }

    [TestMethod]
    public void ServerOptions_ExposesPostProviderOrleansCallback()
    {
        Action<Orleans.Hosting.ISiloBuilder> callback = _ => { };

        var options = new FabrCoreServerOptions().ConfigureOrleans(callback);

        Assert.AreSame(callback, options.PostConfigureOrleans);
    }

    [TestMethod]
    public void GatewayDiscovery_DefaultTlsRequirementDependsOnEnvironment()
    {
        var developmentBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Development" });
        developmentBuilder.AddFabrCoreServices();

        var productionBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Production" });
        productionBuilder.AddFabrCoreServices();

        using var developmentServices = developmentBuilder.Services.BuildServiceProvider();
        using var productionServices = productionBuilder.Services.BuildServiceProvider();
        Assert.IsFalse(developmentServices.GetRequiredService<IOptions<GatewayDiscoveryOptions>>().Value.RequireOrleansTls);
        Assert.IsTrue(productionServices.GetRequiredService<IOptions<GatewayDiscoveryOptions>>().Value.RequireOrleansTls);
    }

    private static ClusterGatewayController CreateController(
        GatewayDiscoveryOptions options,
        bool authenticated = true,
        bool authorizationSucceeded = true,
        IReadOnlyList<Uri>? gateways = null)
    {
        gateways ??= [new Uri("gwy.tcp://10.0.0.1:30000/0")];
        var controller = new ClusterGatewayController(
            new StaticOptionsMonitor<GatewayDiscoveryOptions>(options),
            Options.Create(new ClusterOptions { ClusterId = "cluster-a", ServiceId = "service-a" }),
            new StaticGatewaySource(gateways),
            new StaticAuthorizationService(authorizationSucceeded));

        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "client")], "test")
            : new ClaimsIdentity();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
        return controller;
    }

    private static GatewayDiscoveryOptions EnabledOptions() => new()
    {
        Enabled = true,
        AuthorizationPolicy = "GatewayDiscovery",
        RefreshPeriod = TimeSpan.FromSeconds(30),
        RequireOrleansTls = true
    };

    private static ClusterMember Member(string address, SiloStatus status)
        => new(
            SiloAddress.New(IPAddress.Parse(address), port: 11111, generation: 1),
            status,
            $"silo-{address}");

    private sealed class StaticGatewaySource(IReadOnlyList<Uri> gateways) : IGatewayDiscoverySource
    {
        public IReadOnlyList<Uri> GetGateways() => gateways;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticAuthorizationService(bool succeeded) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
            => Task.FromResult(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}
