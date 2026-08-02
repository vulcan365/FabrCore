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
        builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:ApiKey"] = "forge-key";
        builder.Configuration["FabrCore:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RemoteAdministrationOptions>>().Value;

        Assert.AreEqual("http://curia-ai", options.HostUrl);
        Assert.IsTrue(options.Enabled);
    }

    [TestMethod]
    public void RemoteAdministration_MissingHostUrlFailsValidation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:ApiKey"] = "forge-key";
        builder.Configuration["FabrCore:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.ThrowsExactly<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<RemoteAdministrationOptions>>().Value);
        StringAssert.Contains(exception.Message, "FabrCore:HostUrl");
    }

    [TestMethod]
    public void RemoteAdministration_RequiresCloudServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:HostUrl"] = "http://127.0.0.1:5000";
        builder.Configuration["FabrCore:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.ThrowsExactly<OptionsValidationException>(() =>
            _ = services.GetRequiredService<IOptions<RemoteAdministrationOptions>>().Value);
        StringAssert.Contains(exception.Message, "FabrCore:CloudServer:Enabled");
    }

    [TestMethod]
    public void LegacyNestedRemoteAdministrationSetting_DoesNotEnableFeature()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:ApiKey"] = "forge-key";
        builder.Configuration["FabrCore:CloudServer:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var remoteAdministration = services
            .GetRequiredService<IOptions<RemoteAdministrationOptions>>().Value;

        Assert.IsFalse(remoteAdministration.Enabled);
    }

    [TestMethod]
    public void RemoteAdministration_UsesCloudServerApiKey()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
        builder.Configuration["FabrCore:CloudServer:ApiKey"] = "forge-key";
        builder.Configuration["FabrCore:HostUrl"] = "http://127.0.0.1:5000";
        builder.Configuration["FabrCore:RemoteAdministration:Enabled"] = "true";

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var cloud = services.GetRequiredService<IOptions<CloudServerOptions>>().Value;
        var remoteAdministration = services.GetRequiredService<IOptions<RemoteAdministrationOptions>>().Value;

        Assert.AreEqual("forge-key", cloud.ApiKey);
        Assert.IsTrue(remoteAdministration.Enabled);
    }
}
