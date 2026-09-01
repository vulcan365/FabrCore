using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration.Cloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudSettingsPolicyTests
{
    [TestMethod]
    [DataRow("FabrCore:CloudServer:Url")]
    [DataRow("FabrCore:CloudServer:Enabled")]
    [DataRow("FabrCore:CloudServer:ApiKey")]
    [DataRow("FabrCore:CloudServer")]
    [DataRow("FabrCore:RemoteAdministration:Enabled")]
    [DataRow("FabrCore:HostUrl")]
    // Configuration keys are case-insensitive, so the blocklist must be too.
    [DataRow("fabrcore:cloudserver:url")]
    public void Blocks_enrollment_and_recovery_keys(string key) =>
        Assert.IsTrue(CloudSettingsPolicy.IsBlocked(key), $"{key} must never be settable from the cloud");

    [TestMethod]
    [DataRow("FabrCore:Orleans:ClusterId")]
    [DataRow("ConnectionStrings:MemoryDb")]
    [DataRow("FabrCore:Acl:Mode")]
    // Must match on segment boundaries: this is a different section that merely shares a prefix.
    [DataRow("FabrCore:CloudServerExtras:Thing")]
    public void Allows_everything_else(string key) =>
        Assert.IsFalse(CloudSettingsPolicy.IsBlocked(key));

    [TestMethod]
    public void Filter_separates_accepted_from_blocked()
    {
        var result = CloudSettingsPolicy.Filter(new Dictionary<string, string?>
        {
            ["FabrCore:Orleans:ClusterId"] = "prod",
            ["FabrCore:CloudServer:Url"] = "https://attacker.test"
        });

        Assert.AreEqual(1, result.Accepted.Count);
        Assert.AreEqual("prod", result.Accepted["FabrCore:Orleans:ClusterId"]);
        Assert.AreEqual(1, result.Rejected.Count);
        Assert.AreEqual(CloudSettingRejection.Blocked, result.Rejected[0].Reason);
        Assert.AreEqual("FabrCore:CloudServer:Url", result.Rejected[0].Key);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(":leading")]
    [DataRow("trailing:")]
    [DataRow("double::colon")]
    [DataRow("dot..dot")]
    public void Rejects_malformed_keys(string key)
    {
        var result = CloudSettingsPolicy.Filter(new Dictionary<string, string?> { [key] = "value" });

        Assert.AreEqual(0, result.Accepted.Count);
        Assert.AreEqual(CloudSettingRejection.Malformed, result.Rejected.Single().Reason);
    }

    [TestMethod]
    public void Bounds_a_hostile_payload_by_key_count()
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < CloudSettingsPolicy.MaxKeyCount + 25; i++)
        {
            settings[$"FabrCore:Test:Key{i}"] = "v";
        }

        var result = CloudSettingsPolicy.Filter(settings);

        Assert.AreEqual(CloudSettingsPolicy.MaxKeyCount, result.Accepted.Count);
        Assert.AreEqual(25, result.Rejected.Count);
        Assert.IsTrue(result.Rejected.All(r => r.Reason == CloudSettingRejection.LimitExceeded));
    }

    [TestMethod]
    public void Bounds_a_hostile_payload_by_total_value_length()
    {
        var result = CloudSettingsPolicy.Filter(new Dictionary<string, string?>
        {
            ["FabrCore:Test:Big"] = new('x', CloudSettingsPolicy.MaxTotalValueLength + 1)
        });

        Assert.AreEqual(0, result.Accepted.Count);
        Assert.AreEqual(CloudSettingRejection.LimitExceeded, result.Rejected.Single().Reason);
    }

    [TestMethod]
    public void Null_settings_is_a_no_op()
    {
        var result = CloudSettingsPolicy.Filter(null);

        Assert.AreEqual(0, result.Accepted.Count);
        Assert.AreEqual(0, result.Rejected.Count);
    }
}

[TestClass]
public sealed class CloudSettingsConfigurationProviderTests
{
    private sealed class SampleOptions
    {
        public string Name { get; set; } = "default";
        public int Size { get; set; }
    }

    [TestMethod]
    public void Applied_settings_are_readable_through_IConfiguration()
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Sample:Name"] = "from-cloud" });

        var configuration = new ConfigurationBuilder()
            .Add(new CloudSettingsConfigurationSource(provider))
            .Build();

        Assert.AreEqual("from-cloud", configuration["FabrCore:Sample:Name"]);
    }

    [TestMethod]
    public void Apply_raises_the_change_token_so_IOptionsMonitor_rebinds()
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?>
        {
            ["FabrCore:Sample:Name"] = "before",
            ["FabrCore:Sample:Size"] = "1"
        });

        var configuration = new ConfigurationBuilder()
            .Add(new CloudSettingsConfigurationSource(provider))
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<SampleOptions>().Bind(configuration.GetSection("FabrCore:Sample"));
        using var container = services.BuildServiceProvider();
        var monitor = container.GetRequiredService<IOptionsMonitor<SampleOptions>>();

        Assert.AreEqual("before", monitor.CurrentValue.Name);
        Assert.AreEqual(1, monitor.CurrentValue.Size);

        provider.Apply(new Dictionary<string, string?>
        {
            ["FabrCore:Sample:Name"] = "after",
            ["FabrCore:Sample:Size"] = "2"
        });

        Assert.AreEqual("after", monitor.CurrentValue.Name);
        Assert.AreEqual(2, monitor.CurrentValue.Size);
    }

    [TestMethod]
    public void Apply_replaces_the_layer_rather_than_merging()
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Sample:Gone"] = "value" });
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Sample:Kept"] = "value" });

        var configuration = new ConfigurationBuilder()
            .Add(new CloudSettingsConfigurationSource(provider))
            .Build();

        Assert.IsNull(configuration["FabrCore:Sample:Gone"]);
        Assert.AreEqual("value", configuration["FabrCore:Sample:Kept"]);
    }

    [TestMethod]
    public void Blocked_keys_never_reach_configuration()
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?>
        {
            ["FabrCore:CloudServer:Url"] = "https://attacker.test",
            ["FabrCore:HostUrl"] = "https://attacker.test"
        });

        var configuration = new ConfigurationBuilder()
            .Add(new CloudSettingsConfigurationSource(provider))
            .Build();

        Assert.IsNull(configuration["FabrCore:CloudServer:Url"]);
        Assert.IsNull(configuration["FabrCore:HostUrl"]);
        Assert.AreEqual(2, provider.Rejected.Count);
    }
}

[TestClass]
public sealed class CloudSettingsPrecedenceTests
{
    private static IConfigurationBuilder BuilderWithLocalSources(string appSettingsValue) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCore:Orleans:ClusterId"] = appSettingsValue
            })
            .AddEnvironmentVariables();

    [TestMethod]
    public void Cloud_overrides_appsettings()
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "from-cloud" });

        var builder = BuilderWithLocalSources("from-file");
        CloudSettingsBootstrapper.Insert(
            builder, new CloudSettingsConfigurationSource(provider), NullLogger.Instance);

        Assert.AreEqual("from-cloud", builder.Build()["FabrCore:Orleans:ClusterId"]);
    }

    [TestMethod]
    public void Environment_variables_still_override_cloud()
    {
        // The break-glass property: an operator must be able to correct a bad publish locally,
        // without needing to reach the cloud server that produced it.
        var variable = $"FabrCore__Orleans__ClusterId";
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var provider = new CloudSettingsConfigurationProvider();
            provider.Apply(new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "from-cloud" });

            var builder = BuilderWithLocalSources("from-file");
            CloudSettingsBootstrapper.Insert(
                builder, new CloudSettingsConfigurationSource(provider), NullLogger.Instance);

            Assert.AreEqual("from-env", builder.Build()["FabrCore:Orleans:ClusterId"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public void Cloud_overrides_appsettings_even_with_host_environment_sources_first()
    {
        // Reproduces the real WebApplicationBuilder layout, which carries environment-variable
        // sources on BOTH sides of the appsettings files: DOTNET_/ASPNETCORE_ prefixed host
        // sources first, the unprefixed application source last. Inserting before the FIRST
        // environment source buries the cloud layer under appsettings.json and inverts the
        // intended precedence.
        var builder = new ConfigurationBuilder();
        builder.Sources.Add(new EnvironmentVariablesConfigurationSource { Prefix = "ASPNETCORE_" });
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FabrCore:Orleans:ClusterId"] = "from-file"
        });
        builder.Sources.Add(new EnvironmentVariablesConfigurationSource());

        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "from-cloud" });
        CloudSettingsBootstrapper.Insert(
            builder, new CloudSettingsConfigurationSource(provider), NullLogger.Instance);

        Assert.AreEqual("from-cloud", builder.Build()["FabrCore:Orleans:ClusterId"]);
        Assert.AreEqual(
            2,
            builder.Sources.IndexOf(builder.Sources.OfType<CloudSettingsConfigurationSource>().Single()),
            "The cloud layer belongs after the file sources and before the trailing environment source.");
    }

    [TestMethod]
    public void Cloud_source_is_appended_when_there_are_no_environment_sources()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "from-file" });
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "from-cloud" });

        CloudSettingsBootstrapper.Insert(
            builder, new CloudSettingsConfigurationSource(provider), NullLogger.Instance);

        Assert.AreEqual(1, builder.Sources.Count(source => source is CloudSettingsConfigurationSource));
        Assert.AreEqual("from-cloud", builder.Build()["FabrCore:Orleans:ClusterId"]);
    }
}

[TestClass]
public sealed class FabrCoreSettingsCatalogTests
{
    private sealed class TestContributor : IFabrCoreSettingsCatalogContributor
    {
        public IEnumerable<FabrCoreSettingDescriptor> GetSettings() =>
        [
            new("MyAddon", "section", null, "Test addon.", SettingsApplyMode.Live, IsSection: true)
        ];
    }

    [TestMethod]
    public void Unknown_keys_default_to_restart_required() =>
        Assert.AreEqual(
            SettingsApplyMode.RestartRequired,
            new FabrCoreSettingsCatalog().GetApplyMode("Something:Nobody:Declared"));

    [TestMethod]
    public void Most_specific_descriptor_wins()
    {
        var catalog = new FabrCoreSettingsCatalog();

        // FabrCore:Host is RestartRequired, but its GatewayDiscovery child is observed
        // through IOptionsMonitor and therefore applies live.
        Assert.AreEqual(SettingsApplyMode.RestartRequired, catalog.GetApplyMode("FabrCore:Host:OutboundQueueCapacity"));
        Assert.AreEqual(SettingsApplyMode.Live, catalog.GetApplyMode("FabrCore:Host:GatewayDiscovery:RefreshPeriod"));
    }

    [TestMethod]
    public void Orleans_and_connection_strings_require_a_restart()
    {
        var catalog = new FabrCoreSettingsCatalog();

        Assert.AreEqual(SettingsApplyMode.RestartRequired, catalog.GetApplyMode("FabrCore:Orleans:ClusterId"));
        Assert.AreEqual(SettingsApplyMode.RestartRequired, catalog.GetApplyMode("ConnectionStrings:MemoryDb"));
    }

    [TestMethod]
    public void Contributors_extend_the_catalog()
    {
        var catalog = new FabrCoreSettingsCatalog([new TestContributor()]);

        Assert.AreEqual(SettingsApplyMode.Live, catalog.GetApplyMode("MyAddon:Anything"));
        Assert.IsNotNull(catalog.Find("MyAddon:Anything"));
    }

    [TestMethod]
    [DataRow("ConnectionStrings:MemoryDb")]
    [DataRow("FabrCore:AdminAuthentication:ApiKey")]
    [DataRow("Microsoft365Copilot:ClientSecret")]
    [DataRow("A2A:Authentication:Jwt:Password")]
    public void Secrets_are_recognised(string key) =>
        Assert.IsTrue(FabrCoreSettingsCatalog.IsSecret(key));

    [TestMethod]
    [DataRow("FabrCore:Orleans:ClusterId")]
    [DataRow("FabrCore:Acl:Mode")]
    public void Non_secrets_are_not_flagged(string key) =>
        Assert.IsFalse(FabrCoreSettingsCatalog.IsSecret(key));
}

[TestClass]
public sealed class CloudSettingsStateTests
{
    private static CloudConfigurationEnvelope Envelope(string version, Dictionary<string, string?>? settings) =>
        new() { ConfigurationVersion = version, Settings = settings };

    private static CloudSettingsState StateStartedWith(Dictionary<string, string?>? settings)
    {
        var provider = new CloudSettingsConfigurationProvider();
        provider.Apply(settings);
        return new CloudSettingsState(provider, Envelope("v1", settings));
    }

    [TestMethod]
    public void Nothing_is_pending_at_startup()
    {
        var state = StateStartedWith(new Dictionary<string, string?>
        {
            ["FabrCore:Orleans:ClusterId"] = "prod"
        });

        Assert.AreEqual(0, state.PendingRestartSettings.Count);
        Assert.AreEqual("v1", state.AppliedSettingsVersion);
    }

    [TestMethod]
    public void Changing_a_restart_required_key_after_startup_marks_it_pending()
    {
        var state = StateStartedWith(new Dictionary<string, string?>
        {
            ["FabrCore:Orleans:ClusterId"] = "prod"
        });

        state.Apply(
            Envelope("v2", new Dictionary<string, string?> { ["FabrCore:Orleans:ClusterId"] = "prod-2" }),
            new FabrCoreSettingsCatalog(),
            NullLogger.Instance);

        CollectionAssert.AreEqual(
            new[] { "FabrCore:Orleans:ClusterId" }, state.PendingRestartSettings.ToArray());
        Assert.AreEqual("v2", state.AppliedSettingsVersion);
    }

    [TestMethod]
    public void Changing_a_live_key_after_startup_is_not_pending()
    {
        var state = StateStartedWith(new Dictionary<string, string?>
        {
            ["FabrCore:Host:GatewayDiscovery:RefreshPeriod"] = "00:01:00"
        });

        state.Apply(
            Envelope("v2", new Dictionary<string, string?>
            {
                ["FabrCore:Host:GatewayDiscovery:RefreshPeriod"] = "00:02:00"
            }),
            new FabrCoreSettingsCatalog(),
            NullLogger.Instance);

        Assert.AreEqual(0, state.PendingRestartSettings.Count);
    }

    [TestMethod]
    public void Removing_a_restart_required_key_after_startup_marks_it_pending()
    {
        // The running process still holds the startup value, so reverting to the local default
        // is just as much a pending change as setting a new value.
        var state = StateStartedWith(new Dictionary<string, string?>
        {
            ["FabrCore:Acl:Mode"] = "Enforce"
        });

        state.Apply(Envelope("v2", []), new FabrCoreSettingsCatalog(), NullLogger.Instance);

        CollectionAssert.AreEqual(new[] { "FabrCore:Acl:Mode" }, state.PendingRestartSettings.ToArray());
    }

    [TestMethod]
    public void Reapplying_the_startup_value_clears_the_pending_flag()
    {
        var catalog = new FabrCoreSettingsCatalog();
        var state = StateStartedWith(new Dictionary<string, string?> { ["FabrCore:Acl:Mode"] = "Enforce" });

        state.Apply(
            Envelope("v2", new Dictionary<string, string?> { ["FabrCore:Acl:Mode"] = "Audit" }),
            catalog, NullLogger.Instance);
        Assert.AreEqual(1, state.PendingRestartSettings.Count);

        state.Apply(
            Envelope("v3", new Dictionary<string, string?> { ["FabrCore:Acl:Mode"] = "Enforce" }),
            catalog, NullLogger.Instance);
        Assert.AreEqual(0, state.PendingRestartSettings.Count);
    }

    [TestMethod]
    public void Bootstrap_envelope_is_handed_out_exactly_once()
    {
        var state = StateStartedWith([]);

        Assert.IsNotNull(state.TakeBootstrapEnvelope());
        Assert.IsNull(state.TakeBootstrapEnvelope());
    }
}
