using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ConfiguredPluginLifetimeTests
{
    [TestMethod]
    public async Task NormalProxyDisposesInheritedPluginsAndKeepsOptionalAliasBehavior()
    {
        var tracker = new Tracker();
        using var services = Services(tracker);
        var config = new AgentConfiguration { Handle = "owner:scripts", Plugins = ["lifetime-test", "missing-plugin"], Tools = ["missing-tool"] };
        var proxy = new TestProxy(config, services, new FakeAgentHost("owner:scripts"));
        var tools = await proxy.Resolve();
        Assert.HasCount(1, tools);
        Assert.AreEqual("InheritedTool", tools[0].Name);
        Assert.AreEqual(1, tracker.Initialized);
        await ((IFabrCoreAgentProxy)proxy).InternalDisposeAsync();
        await ((IFabrCoreAgentProxy)proxy).InternalDisposeAsync();
        Assert.AreEqual(1, tracker.Disposed);
    }

    [TestMethod]
    public async Task FailedResolutionDisposesPreviouslyInitializedPlugins()
    {
        var tracker = new Tracker();
        using var services = Services(tracker);
        var registry = services.GetRequiredService<FabrCoreToolRegistry>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ResolveToolScopeAsync(services,
            ["lifetime-test", "missing-plugin"], [], new()));
        Assert.AreEqual(1, tracker.Disposed);
    }

    [TestMethod]
    public async Task CleanupContinuesWhenOnePluginThrows()
    {
        var tracker = new Tracker();
        var scope = new FabrCoreResolvedToolScope([], [new Disposable(tracker), new ThrowingDisposable()]);
        await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.AreEqual(1, tracker.Disposed);
        await scope.DisposeAsync();
    }

    private static ServiceProvider Services(Tracker tracker) => new ServiceCollection()
        .AddSingleton(tracker)
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton<IFabrCoreChatClientService>(new FakeChatClientService(FakeChatClient.WithTextResponse("unused")))
        .AddSingleton(new FabrCoreToolRegistry(NullLogger<FabrCoreToolRegistry>.Instance, [typeof(TestPlugin).Assembly]))
        .BuildServiceProvider();

    public sealed class Tracker { public int Initialized; public int Disposed; }
    public abstract class TestPluginBase(Tracker tracker) : IFabrCorePlugin, IAsyncDisposable
    {
        public Task InitializeAsync(AgentConfiguration config, IServiceProvider services) { tracker.Initialized++; return Task.CompletedTask; }
        [System.ComponentModel.Description("Inherited test tool")]
        public string InheritedTool() => "ok";
        public ValueTask DisposeAsync() { tracker.Disposed++; return ValueTask.CompletedTask; }
    }
    [PluginAlias("lifetime-test")]
    public sealed class TestPlugin(Tracker tracker) : TestPluginBase(tracker);
    private sealed class Disposable(Tracker tracker) : IDisposable { public void Dispose() => tracker.Disposed++; }
    private sealed class ThrowingDisposable : IDisposable { public void Dispose() => throw new InvalidOperationException("cleanup"); }
    private sealed class TestProxy(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
        : FabrCoreAgentProxy(config, services, host)
    {
        public Task<List<AITool>> Resolve() => ResolveConfiguredToolsAsync();
        public override Task OnInitialize() => Task.CompletedTask;
        public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
    }
}
