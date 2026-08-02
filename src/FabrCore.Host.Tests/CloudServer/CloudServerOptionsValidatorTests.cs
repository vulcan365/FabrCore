using FabrCore.Host.Configuration;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerOptionsValidatorTests
{
    private static readonly CloudServerOptionsValidator Validator = new();

    private static Microsoft.Extensions.Options.ValidateOptionsResult ValidateRemote(
        CloudServerOptions cloud,
        RemoteAdministrationOptions remote) =>
        new RemoteAdministrationOptionsValidator(Microsoft.Extensions.Options.Options.Create(cloud))
            .Validate(null, remote);

    [TestMethod]
    public void Disabled_AlwaysValid()
    {
        var options = new CloudServerOptions { Enabled = false, Url = "not a url", ApiKey = null };
        Assert.IsTrue(Validator.Validate(null, options).Succeeded);
    }

    [TestMethod]
    public void Enabled_WithDefaults_AndApiKey_IsValid()
    {
        var options = new CloudServerOptions { Enabled = true, ApiKey = "frg_abc" };
        Assert.IsTrue(Validator.Validate(null, options).Succeeded);
    }

    [TestMethod]
    public void Enabled_WithoutApiKey_Fails()
    {
        var options = new CloudServerOptions { Enabled = true };
        var result = Validator.Validate(null, options);
        Assert.IsTrue(result.Failed);
        Assert.IsTrue(result.FailureMessage!.Contains("ApiKey"));
    }

    [TestMethod]
    public void Enabled_WithInvalidUrl_Fails()
    {
        var options = new CloudServerOptions { Enabled = true, ApiKey = "k", Url = "ftp://forge" };
        var result = Validator.Validate(null, options);
        Assert.IsTrue(result.Failed);
        Assert.IsTrue(result.FailureMessage!.Contains("Url"));
    }

    [TestMethod]
    public void Enabled_WithNonPositiveIntervals_Fails()
    {
        var options = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "k",
            RefreshInterval = TimeSpan.Zero,
            RequestTimeout = TimeSpan.Zero,
            Heartbeat = { Interval = TimeSpan.Zero }
        };
        var result = Validator.Validate(null, options);
        Assert.IsTrue(result.Failed);
        Assert.IsTrue(result.FailureMessage!.Contains("RefreshInterval"));
        Assert.IsTrue(result.FailureMessage.Contains("RequestTimeout"));
        Assert.IsTrue(result.FailureMessage.Contains("Heartbeat"));
    }

    [TestMethod]
    public void Enabled_WithDisabledHeartbeat_IgnoresHeartbeatInterval()
    {
        var options = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "k",
            Heartbeat = { Enabled = false, Interval = TimeSpan.Zero }
        };
        Assert.IsTrue(Validator.Validate(null, options).Succeeded);
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_WithHostUrlAndCloudServerKey_IsValid()
    {
        var cloud = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "forge-key"
        };
        var remote = new RemoteAdministrationOptions
            { Enabled = true, HostUrl = "http://127.0.0.1:5000" };

        Assert.IsTrue(ValidateRemote(cloud, remote).Succeeded);
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_AcceptsRemoteAndHttpsHostUrl()
    {
        foreach (var url in new[] { "http://host.internal:5000", "https://127.0.0.1:5000", "http://curia-ai" })
        {
            var cloud = new CloudServerOptions
            {
                Enabled = true,
                ApiKey = "forge-key"
            };
            var remote = new RemoteAdministrationOptions { Enabled = true, HostUrl = url };

            var result = ValidateRemote(cloud, remote);
            Assert.IsTrue(result.Succeeded, $"Expected '{url}' to be accepted.");
        }
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_RejectsMissingOrNonHttpHostUrl()
    {
        foreach (var url in new string?[] { null, "", "not a url", "ftp://x" })
        {
            var cloud = new CloudServerOptions
            {
                Enabled = true,
                ApiKey = "forge-key"
            };
            var remote = new RemoteAdministrationOptions { Enabled = true, HostUrl = url };

            var result = ValidateRemote(cloud, remote);
            Assert.IsTrue(result.Failed, $"Expected '{url}' to be rejected.");
            Assert.IsTrue(result.FailureMessage!.Contains("FabrCore:HostUrl"));
        }
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_RequiresSafeLimits()
    {
        var cloud = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "forge-key"
        };
        var remote = new RemoteAdministrationOptions
        {
            Enabled = true,
            HostUrl = "http://127.0.0.1:5000",
            PollWait = TimeSpan.FromSeconds(56),
            MaxBodyBytes = 512
        };

        var result = ValidateRemote(cloud, remote);

        Assert.IsTrue(result.Failed);
        Assert.IsTrue(result.FailureMessage!.Contains("PollWait"));
        Assert.IsTrue(result.FailureMessage.Contains("MaxBodyBytes"));
    }
}
