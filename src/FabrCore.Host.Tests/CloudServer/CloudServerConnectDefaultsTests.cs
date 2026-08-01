using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerConnectDefaultsTests
{
    [TestMethod]
    public void ConnectLocalAdminUrl_DefaultsToFabrCoreHostUrl()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:HostUrl"] = "http://curia-ai";
        builder.Configuration["FabrCore:CloudServer:Connect:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("http://curia-ai", options.Connect.LocalAdminUrl);
    }

    [TestMethod]
    public void ConnectLocalAdminUrl_FallsBackToLoopback_WhenHostUrlUnset()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:CloudServer:Connect:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("http://127.0.0.1:5000", options.Connect.LocalAdminUrl);
    }

    [TestMethod]
    public void ConnectLocalAdminUrl_ExplicitValueWins()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:HostUrl"] = "http://curia-ai";
        builder.Configuration["FabrCore:CloudServer:Connect:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:Connect:LocalAdminUrl"] = "http://127.0.0.1:8443";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("http://127.0.0.1:8443", options.Connect.LocalAdminUrl);
    }

    [TestMethod]
    public void ConnectLocalAdminApiKey_DefaultsToAdminAuthenticationApiKey()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:AdminAuthentication:ApiKey"] = "local-admin-key";
        builder.Configuration["FabrCore:CloudServer:Connect:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("local-admin-key", options.Connect.LocalAdminApiKey);
    }
}
