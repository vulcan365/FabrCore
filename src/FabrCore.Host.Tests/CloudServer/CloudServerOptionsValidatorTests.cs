using FabrCore.Host.Configuration;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerOptionsValidatorTests
{
    private static readonly CloudServerOptionsValidator Validator = new();

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
    public void RemoteAdministrationEnabled_WithHostUrlAndAdminKey_IsValid()
    {
        var options = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "forge-key",
            HostUrl = "http://127.0.0.1:5000",
            RemoteAdministration =
            {
                Enabled = true,
                LocalAdminApiKey = "local-admin-key"
            }
        };

        Assert.IsTrue(Validator.Validate(null, options).Succeeded);
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_AcceptsRemoteAndHttpsHostUrl()
    {
        foreach (var url in new[] { "http://host.internal:5000", "https://127.0.0.1:5000", "http://curia-ai" })
        {
            var options = new CloudServerOptions
            {
                Enabled = true,
                ApiKey = "forge-key",
                HostUrl = url,
                RemoteAdministration =
                {
                    Enabled = true,
                    LocalAdminApiKey = "local-admin-key"
                }
            };

            var result = Validator.Validate(null, options);
            Assert.IsTrue(result.Succeeded, $"Expected '{url}' to be accepted.");
        }
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_RejectsMissingOrNonHttpHostUrl()
    {
        foreach (var url in new string?[] { null, "", "not a url", "ftp://x" })
        {
            var options = new CloudServerOptions
            {
                Enabled = true,
                ApiKey = "forge-key",
                HostUrl = url,
                RemoteAdministration =
                {
                    Enabled = true,
                    LocalAdminApiKey = "local-admin-key"
                }
            };

            var result = Validator.Validate(null, options);
            Assert.IsTrue(result.Failed, $"Expected '{url}' to be rejected.");
            Assert.IsTrue(result.FailureMessage!.Contains("FabrCore:HostUrl"));
        }
    }

    [TestMethod]
    public void RemoteAdministrationEnabled_RequiresAdminKeyAndSafeLimits()
    {
        var options = new CloudServerOptions
        {
            Enabled = true,
            ApiKey = "forge-key",
            HostUrl = "http://127.0.0.1:5000",
            RemoteAdministration =
            {
                Enabled = true,
                LocalAdminApiKey = null,
                PollWait = TimeSpan.FromSeconds(56),
                MaxBodyBytes = 512
            }
        };

        var result = Validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
        Assert.IsTrue(result.FailureMessage!.Contains("LocalAdminApiKey"));
        Assert.IsTrue(result.FailureMessage.Contains("PollWait"));
        Assert.IsTrue(result.FailureMessage.Contains("MaxBodyBytes"));
    }
}
