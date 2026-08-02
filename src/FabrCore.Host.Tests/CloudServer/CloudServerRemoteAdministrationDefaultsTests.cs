using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerRemoteAdministrationDefaultsTests
{
    [TestMethod]
    public void RemoteAdministration_UsesFabrCoreHostUrl()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:HostUrl"] = "http://curia-ai";
        builder.Configuration["FabrCore:CloudServer:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("http://curia-ai", options.HostUrl);
        Assert.IsTrue(options.RemoteAdministration.Enabled);
    }

    [TestMethod]
    public void RemoteAdministration_MissingHostUrlFailsValidation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:ApiKey"] = "forge-key";
        builder.Configuration["FabrCore:AdminAuthentication:ApiKey"] = "local-admin-key";
        builder.Configuration["FabrCore:CloudServer:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.ThrowsExactly<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<CloudServerOptions>>().Value);
        StringAssert.Contains(exception.Message, "FabrCore:HostUrl");
    }

    [TestMethod]
    public void RemoteAdministrationLocalAdminApiKey_DefaultsToAdminAuthenticationApiKey()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:AdminAuthentication:ApiKey"] = "local-admin-key";
        builder.Configuration["FabrCore:CloudServer:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;

        Assert.AreEqual("local-admin-key", options.RemoteAdministration.LocalAdminApiKey);
    }
}
