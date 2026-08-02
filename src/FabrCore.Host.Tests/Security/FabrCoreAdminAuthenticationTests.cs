using FabrCore.Host.Configuration;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests.Security;

[TestClass]
public sealed class FabrCoreAdminAuthenticationTests
{
    [TestMethod]
    public async Task CloudServerApiKey_IsAccepted_WhenRemoteAdministrationIsEnabled()
    {
        var result = await AuthenticateAsync(
            suppliedKey: "cloud-key",
            adminKey: null,
            cloudEnabled: true,
            remoteAdministrationEnabled: true);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task CloudServerApiKey_IsRejected_WhenRemoteAdministrationIsDisabled()
    {
        var result = await AuthenticateAsync(
            suppliedKey: "cloud-key",
            adminKey: null,
            cloudEnabled: true,
            remoteAdministrationEnabled: false);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task CloudServerApiKey_IsRejected_WhenCloudServerIsDisabled()
    {
        var result = await AuthenticateAsync(
            suppliedKey: "cloud-key",
            adminKey: null,
            cloudEnabled: false,
            remoteAdministrationEnabled: true);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task AdminAuthenticationApiKey_RemainsAccepted()
    {
        var result = await AuthenticateAsync(
            suppliedKey: "admin-key",
            adminKey: "admin-key",
            cloudEnabled: false,
            remoteAdministrationEnabled: false);

        Assert.IsTrue(result.Succeeded);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        string suppliedKey,
        string? adminKey,
        bool cloudEnabled,
        bool remoteAdministrationEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CloudServerOptions>(options =>
        {
            options.Enabled = cloudEnabled;
            options.ApiKey = "cloud-key";
        });
        services.Configure<RemoteAdministrationOptions>(options =>
            options.Enabled = remoteAdministrationEnabled);
        services
            .AddAuthentication()
            .AddScheme<FabrCoreAdminAuthenticationOptions, FabrCoreAdminAuthenticationHandler>(
                FabrCoreAdminAuthenticationDefaults.Scheme,
                options => options.ApiKey = adminKey);

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Bearer {suppliedKey}";

        return await provider.GetRequiredService<IAuthenticationService>().AuthenticateAsync(
            context,
            FabrCoreAdminAuthenticationDefaults.Scheme);
    }
}
