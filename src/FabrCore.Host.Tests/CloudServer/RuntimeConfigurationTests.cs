using System.Text.Json;
using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration.Cloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using FabrCore.Host.Configuration;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class RuntimeConfigurationTests
{
    private const string Key = "FabrCore:Orleans:ClusteringMode";
    public sealed class ObservedOptions { public int Limit { get; set; } = 10; }

    [TestMethod]
    public void LegacyFileFallback_CannotOverrideCloudOrOperator_AndIsRegisteredOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fabrcore-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "fabrcore.json"), """{"FabrCore":{"Orleans":{"ClusteringMode":"Localhost"}},"A2A":{"Enabled":false}}""");
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory });
            var cloud = new CloudSettingsConfigurationProvider();
            cloud.Apply(new Dictionary<string, string?> { [Key] = "SqlServer" });
            ((IConfigurationBuilder)builder.Configuration).Add(new CloudSettingsConfigurationSource(cloud));
            FabrCore.Host.A2A.A2AExtensions.AddLegacyConfigurationFallback(builder);
            Assert.AreEqual("SqlServer", builder.Configuration[Key]);
            builder.Configuration.AddCommandLine([$"--{Key}=AzureStorage"]);
            FabrCore.Host.A2A.A2AExtensions.AddLegacyConfigurationFallback(builder);
            Assert.AreEqual("AzureStorage", builder.Configuration[Key]);
            Assert.AreEqual(1, builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>().Count(s => s.Path == "fabrcore.json"));
            using var host = builder.Build();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void OptionsSnapshot_ObservesOnlyConsumerAccess_AndRetainsAppliedValueOnRefresh()
    {
        const string section = "FabrCore:Host";
        const string key = section + ":Limit";
        using var f = new Fixture(new() { [key] = "20" });
        var services = new ServiceCollection().AddSingleton(f.Runtime);
        services.AddOptions<ObservedOptions>().Bind(f.Configuration.GetSection(section));
        RuntimeOptionsObservation.AddSnapshot<ObservedOptions>(services, section);
        using var provider = services.BuildServiceProvider();
        f.Runtime.MarkStarted(provider);
        Assert.IsFalse(f.Runtime.Report(provider).Settings.Single(s => s.Key == key).AppliedKnown);
        var options = provider.GetRequiredService<IOptions<ObservedOptions>>();
        Assert.AreEqual(20, options.Value.Limit);
        f.Cloud.Apply(new() { ConfigurationVersion = "v2", Settings = new() { [key] = "30" } }, new FabrCoreSettingsCatalog(), NullLogger.Instance);
        Assert.AreEqual(20, options.Value.Limit);
        var row = f.Runtime.Report(provider).Settings.Single(s => s.Key == key);
        Assert.AreEqual("30", row.ResolvedValue);
        Assert.AreEqual("20", row.AppliedValue);
        Assert.IsTrue(row.PendingRestart);
    }

    [TestMethod]
    public void OptionsMonitor_TracksSuccessfulConsumerChanges_AndKeepsNamedOptionsSeparate()
    {
        const string section = "FabrCore:Host:GatewayDiscovery";
        const string key = section + ":Limit";
        using var f = new Fixture(new() { [key] = "20" });
        var services = new ServiceCollection().AddSingleton(f.Runtime);
        services.AddOptions<ObservedOptions>().Bind(f.Configuration.GetSection(section));
        services.AddOptions<ObservedOptions>("special").Configure(o => o.Limit = 99);
        RuntimeOptionsObservation.AddMonitor<ObservedOptions>(services, section);
        using var provider = services.BuildServiceProvider();
        f.Runtime.MarkStarted(provider);
        var options = provider.GetRequiredService<IOptionsMonitor<ObservedOptions>>();
        Assert.AreEqual(20, options.CurrentValue.Limit);
        Assert.AreEqual(99, options.Get("special").Limit);
        var consumed = 20;
        using var subscription = options.OnChange((o, _) => consumed = o.Limit);
        f.Cloud.Apply(new() { ConfigurationVersion = "v2", Settings = new() { [key] = "30" } }, new FabrCoreSettingsCatalog(), NullLogger.Instance);
        Assert.AreEqual(30, consumed);
        var report = f.Runtime.Report(provider);
        var row = report.Settings.Single(s => s.Key == key);
        Assert.AreEqual("30", row.AppliedValue);
        Assert.AreEqual("cloud", row.Source);
        Assert.IsFalse(row.PendingRestart);
        Assert.AreEqual("v2", row.AppliedRevision);
        var named = report.Settings.Single(s => s.Key == "Runtime:Options:ObservedOptions:special:Limit");
        Assert.AreEqual("99", named.AppliedValue);
        Assert.IsFalse(named.CanAdopt);
    }

    [TestMethod]
    public void OptionsMonitor_FailedConsumerIsUnverified()
    {
        const string section = "FabrCore:Host:GatewayDiscovery";
        const string key = section + ":Limit";
        using var f = new Fixture(new() { [key] = "20" });
        var services = new ServiceCollection().AddSingleton(f.Runtime);
        services.AddOptions<ObservedOptions>().Bind(f.Configuration.GetSection(section));
        RuntimeOptionsObservation.AddMonitor<ObservedOptions>(services, section);
        using var provider = services.BuildServiceProvider();
        f.Runtime.MarkStarted(provider);
        var options = provider.GetRequiredService<IOptionsMonitor<ObservedOptions>>();
        _ = options.CurrentValue;
        using var subscription = options.OnChange((_, _) => throw new InvalidOperationException("Consumer failed"));
        Assert.Throws<Exception>(() => f.Cloud.Apply(new() { ConfigurationVersion = "v2", Settings = new() { [key] = "30" } }, new FabrCoreSettingsCatalog(), NullLogger.Instance));
        var report = f.Runtime.Report(provider);
        Assert.IsFalse(report.Settings.Single(s => s.Key == key).AppliedKnown);
        Assert.AreEqual("v2", report.RejectedRevision);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ConfigurationManager Configuration = new();
        internal readonly CloudSettingsState Cloud;
        internal readonly RuntimeConfigurationState Runtime;
        internal readonly ServiceProvider Services;
        internal Fixture(Dictionary<string, string?>? values = null, RuntimeConfigurationRule[]? rules = null,
            Action<ConfigurationManager>? configure = null)
        {
            Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [Key] = "SqlServer" });
            var provider = new CloudSettingsConfigurationProvider();
            provider.Apply(values);
            ((IConfigurationBuilder)Configuration).Add(new CloudSettingsConfigurationSource(provider));
            configure?.Invoke(Configuration);
            Cloud = new(provider, new() { ConfigurationVersion = "v1", Settings = values });
            Runtime = new(Configuration, "impact-test", rules ?? [], Cloud);
            Services = new ServiceCollection().AddSingleton(new FabrCoreSettingsCatalog()).BuildServiceProvider();
            Cloud.AttachServices(Services);
        }
        internal void Started(string? value = "SqlServer")
        { Runtime.Capture(new(Key, value, "Actual SQL membership")); Runtime.MarkStarted(Services); }
        internal void Apply(string? mode) => Cloud.Apply(new() { ConfigurationVersion = "v2", Settings = new() { [Key] = mode } },
            new FabrCoreSettingsCatalog(), NullLogger.Instance);
        internal CloudRuntimeSetting Row() => Runtime.Report(Services).Settings.Single(s => s.Key == Key);
        public void Dispose() { Services.Dispose(); ((IDisposable)Configuration).Dispose(); }
    }

    [TestMethod]
    public void CodeOverride_IsVisibleEvenWhenEqual_AndMasksCloudRestart()
    {
        using var f = new Fixture(new() { [Key] = "SqlServer" },
            [new("Impact.Hosting", c => c.Override(Key, "SqlServer", "Shared durable state"))]);
        f.Started();
        Assert.AreEqual("code-override", f.Row().Source);
        Assert.AreEqual("Impact.Hosting", f.Row().SourceId);
        Assert.IsTrue(f.Row().Overridden);
        f.Apply("Localhost");
        var row = f.Row();
        Assert.AreEqual("Localhost", row.DesiredValue);
        Assert.AreEqual("SqlServer", row.ResolvedValue);
        Assert.AreEqual("SqlServer", row.AppliedValue);
        Assert.IsFalse(row.PendingRestart);
        Assert.HasCount(0, f.Cloud.PendingRestartSettings);
    }

    [TestMethod]
    public void ChangedResolvedSetting_RetainsAppliedSnapshotUntilRestart()
    {
        using var f = new Fixture(); f.Started(); f.Apply("AzureStorage");
        Assert.AreEqual("AzureStorage", f.Row().ResolvedValue);
        Assert.AreEqual("SqlServer", f.Row().AppliedValue);
        Assert.IsTrue(f.Row().PendingRestart);
        CollectionAssert.AreEqual(new[] { Key }, f.Cloud.PendingRestartSettings.ToArray());
    }

    [TestMethod]
    public void Constraints_RejectCandidateBeforePublishingOrNotifyingConsumers()
    {
        using var f = new Fixture(rules: [new("Durability", c => c.Require(Key, value => value == "SqlServer", "SQL is required"))]);
        f.Started();
        var changes = 0;
        using var token = ((IConfiguration)f.Configuration).GetReloadToken().RegisterChangeCallback(_ => changes++, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Apply("Localhost"));
        Assert.AreEqual("SqlServer", f.Configuration[Key]);
        Assert.AreEqual("v1", f.Cloud.AppliedSettingsVersion);
        Assert.AreEqual(0, changes);
        Assert.AreEqual("v2", f.Runtime.Report(f.Services).RejectedRevision);
    }

    [TestMethod]
    public void Preview_IsPureAndUsesCandidateWithoutChangingCloudOrCodeOutputs()
    {
        using var f = new Fixture(rules: [new("Conditional", c =>
        {
            if (c.Get("FabrCore:Database:ConnectionStringName") == "durable") c.Override(Key, "SqlServer", "SQL selected");
        })]);
        f.Started();
        var preview = f.Runtime.Preview(new Dictionary<string, string?> { [Key] = "AzureStorage" }, f.Services);
        Assert.AreEqual("AzureStorage", preview.Settings.Single(s => s.Key == Key).ResolvedValue);
        Assert.AreEqual("SqlServer", f.Configuration[Key]);
        Assert.AreEqual("v1", f.Cloud.AppliedSettingsVersion);
        Assert.IsFalse(f.Row().PendingRestart);
    }

    [TestMethod]
    public void Defaults_RespectExplicitNullAndReset_OperatorOverridesKeepOwnership()
    {
        const string custom = "FabrCore:Host:WebSocketPath";
        using var f = new Fixture(new() { [custom] = null }, [new("Defaults", c => c.Default(custom, "/custom", "App default"))]);
        Assert.IsNull(f.Configuration[custom]);
        f.Cloud.Apply(new() { ConfigurationVersion = "v2", Settings = [] }, new FabrCoreSettingsCatalog(), NullLogger.Instance);
        Assert.AreEqual("/custom", f.Configuration[custom]);
        using var op = new Fixture(rules: [new("Code", c => c.Override(Key, "SqlServer", "Code choice"))],
            configure: c => c.AddCommandLine([$"--{Key}=Localhost"]));
        Assert.AreEqual("Localhost", op.Configuration[Key]);
        Assert.AreEqual("operator", op.Row().Source);
    }

    [TestMethod]
    public void DerivedSqlAndUnobservableConsumers_AreNotReplacedBySchemaExamples()
    {
        using var f = new Fixture();
        f.Configuration[Key] = null;
        f.Runtime.Capture(new(Key, null, "Custom.Provider", AppliedKnown: false));
        f.Runtime.MarkStarted(f.Services);
        Assert.IsFalse(f.Row().AppliedKnown);
        Assert.IsNull(f.Row().AppliedValue);
        Assert.IsFalse(f.Row().PendingRestart);
    }

    [TestMethod]
    public void Reports_RedactSecrets_BoundPayloads_AndNeverClaimPrestartupApplication()
    {
        using var f = new Fixture(new() { ["ConnectionStrings:Database"] = "do-not-report" });
        f.Runtime.Capture(new("ConnectionStrings:Database", "do-not-report", "SQL", Secret: true));
        var before = f.Runtime.Report(f.Services);
        Assert.IsFalse(before.Started);
        Assert.IsFalse(before.Settings.Any(s => s.AppliedKnown));
        f.Runtime.MarkStarted(f.Services);
        Assert.DoesNotContain("do-not-report", JsonSerializer.Serialize(f.Runtime.Report(f.Services)));
        for (var i = 0; i < 400; i++) f.Configuration[$"FabrCore:Host:Setting{i}"] = "value";
        var bounded = f.Runtime.Report(f.Services);
        Assert.IsTrue(bounded.Truncated);
        Assert.IsTrue(bounded.Settings.Count <= 256);
    }

    [TestMethod]
    public void CompetingRulesFailWithAnOwnershipDiagnostic()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new Fixture(rules:
            [new("One", c => c.Override(Key, "SqlServer", "One")), new("Two", c => c.Override(Key, "Localhost", "Two"))]));
        StringAssert.Contains(ex.Message, "competing code owners");
    }

    [TestMethod]
    public void SqlProvider_IsObservedFromRegisteredRuntime_WithoutClusteringModeInJson()
    {
        using var host = new HostBuilder().UseOrleans(silo =>
        {
            silo.UseAdoNetClustering(o => { o.Invariant = "Microsoft.Data.SqlClient"; o.ConnectionString = "Server=unused;Database=unused;Integrated Security=true"; });
            silo.AddMemoryGrainStorage(FabrCoreOrleansConstants.StorageProviderName);
            silo.AddMemoryGrainStorage(FabrCoreOrleansConstants.PubSubStoreName);
            silo.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
            silo.UseInMemoryReminderService();
        }).Build();
        var config = new ConfigurationManager();
        var state = new RuntimeConfigurationState(config, "impact-test", [], null);
        state.Derived(Key, "SqlServer", "Database-derived SQL mode");
        state.CaptureInfrastructure = BuiltInRuntimeObservations.CaptureOrleans;
        state.MarkStarted(host.Services);
        var report = state.Report(host.Services);
        var row = report.Settings.Single(s => s.Key == Key);
        Assert.IsNull(config[Key]);
        Assert.IsTrue(row.AppliedKnown, report.Error);
        Assert.AreEqual("SqlServer", row.AppliedValue);
        Assert.AreEqual("derived", row.Source);
        Assert.IsFalse(row.PendingRestart);
        Assert.IsTrue(report.Settings.Any(s => s.Key == "Runtime:Orleans:GrainStorage" && s.AppliedValue!.Contains("Memory")));
    }

    [TestMethod]
    public void ActualPostConfiguredOptions_AreReportedWithoutReplayingStartupCode()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["A2A:Enabled"] = "false" });
        var state = new RuntimeConfigurationState(config, "test", [], null);
        var services = new ServiceCollection();
        services.AddOptions<A2AOptions>().Bind(config.GetSection("A2A"));
        var calls = 0;
        services.PostConfigure<A2AOptions>(o => { calls++; o.Enabled = true; });
        services.AddSingleton<IFabrCoreRuntimeSettingsContributor, A2ARuntimeSettingsContributor>();
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<A2AOptions>>().Value;
        state.MarkStarted(provider);
        var row = state.Report(provider).Settings.Single(s => s.Key == "A2A:Enabled");
        Assert.AreEqual("True", row.AppliedValue);
        Assert.AreEqual("code-or-consumer", row.Source);
        _ = state.Report(provider);
        Assert.ThrowsExactly<InvalidOperationException>(() => state.Preview(new Dictionary<string, string?> { ["A2A:Enabled"] = "false" }, provider));
        Assert.AreEqual(1, calls);
    }
}
