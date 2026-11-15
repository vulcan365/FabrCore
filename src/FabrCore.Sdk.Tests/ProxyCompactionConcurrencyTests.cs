using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ProxyCompactionConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<string?> Dispatch = new();

    [TestMethod]
    public async Task NormalHandlersCanOverlapWithoutCompaction()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstStart = fixture.Start("first");
        var first = firstStart.Unwrap();
        Task<AgentMessage>? second = null;
        try
        {
            await firstStart.WaitAsync(Timeout);
            await fixture.Proxy.Entered("first").WaitAsync(Timeout);
            var secondStart = fixture.Start("second");
            second = secondStart.Unwrap();
            await secondStart.WaitAsync(Timeout);
            await fixture.Proxy.Entered("second").WaitAsync(Timeout);
            Assert.AreEqual(2, fixture.Proxy.ActiveHandlers);
            Assert.AreEqual(0, fixture.Proxy.CompactionCount);
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await Task.WhenAll(first, second).WaitAsync(Timeout);
        }
        finally
        {
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await DrainAsync(first, second);
        }
    }

    [TestMethod]
    public async Task PreflightNeverCompactsWhileAnotherHandlerIsActive()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstStart = fixture.Start("first");
        var first = firstStart.Unwrap();
        Task<AgentMessage>? second = null;
        Task<AgentMessage>? third = null;
        try
        {
            await firstStart.WaitAsync(Timeout);
            await fixture.Proxy.Entered("first").WaitAsync(Timeout);
            fixture.SeedOversizedHistory();
            var secondStart = fixture.Start("second");
            second = secondStart.Unwrap();
            await secondStart.WaitAsync(Timeout);
            // The invocation has returned after synchronous progress; it may be waiting at a gate.
            Assert.IsFalse(fixture.Proxy.Exited("first").IsCompleted);
            Assert.AreEqual(0, fixture.Proxy.Overlaps);
            await Task.Run(() => fixture.Proxy.Release("first")).WaitAsync(Timeout);
            await fixture.Proxy.Entered("second").WaitAsync(Timeout);
            Assert.AreEqual(0, fixture.Proxy.Overlaps, "The second preflight ran during the first handler.");
            await Task.Run(() => fixture.Proxy.Release("second")).WaitAsync(Timeout);
            await Task.WhenAll(first, second).WaitAsync(Timeout);

            // A quiet turn must still perform preflight, even if the overlapping turn skipped it.
            third = fixture.Start("third").Unwrap();
            await fixture.Proxy.Entered("third").WaitAsync(Timeout);
            Assert.IsTrue(fixture.Proxy.CompactionFor("third").IsCompleted,
                "Oversized history must trigger preflight before a quiet handler.");
            await Task.Run(() => fixture.Proxy.Release("third")).WaitAsync(Timeout);
            await third.WaitAsync(Timeout);
            Assert.AreEqual(0, fixture.Proxy.Overlaps);
        }
        finally
        {
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await DrainAsync(first, second, third);
        }
    }

    [TestMethod]
    public async Task PostflightNeverCompactsWhileAnotherHandlerIsActive()
    {
        using var fixture = await Fixture.CreateAsync(staleAfterMinutes: 0);
        var firstStart = fixture.Start("first");
        var first = firstStart.Unwrap();
        Task<AgentMessage>? second = null;
        try
        {
            await firstStart.WaitAsync(Timeout);
            await fixture.Proxy.Entered("first").WaitAsync(Timeout);
            var secondStart = fixture.Start("second");
            second = secondStart.Unwrap();
            await secondStart.WaitAsync(Timeout);
            await fixture.Proxy.Entered("second").WaitAsync(Timeout);
            fixture.SeedOversizedHistory();
            // The handler release and dispatch await normally run inline on the default scheduler.
            // Release returns after postflight completes or suspends waiting for a quiet point.
            var releaseThread = await Task.Run(() =>
            {
                fixture.Proxy.Release("first");
                return Environment.CurrentManagedThreadId;
            }).WaitAsync(Timeout);
            Assert.IsTrue(fixture.Proxy.Exited("first").IsCompleted);
            Assert.AreEqual(releaseThread, fixture.Proxy.ExitThread("first"), "Handler continuation must run inline.");
            Assert.AreEqual(1, fixture.Proxy.ActiveHandlers);
            Assert.AreEqual(0, fixture.Proxy.Overlaps, "Postflight ran while the second handler was active.");
            await Task.Run(() => fixture.Proxy.Release("second")).WaitAsync(Timeout);
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            Assert.IsTrue(fixture.Proxy.CompactionCount > 0, "Oversized history should eventually be compacted.");
            Assert.AreEqual(0, fixture.Proxy.Overlaps);
        }
        finally
        {
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await DrainAsync(first, second);
        }
    }

    [TestMethod]
    public async Task HandlerCannotEnterDuringPreflightCompaction()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.SeedOversizedHistory();
        fixture.Proxy.BlockCompaction = true;
        var firstStart = fixture.Start("first");
        var first = firstStart.Unwrap();
        Task<AgentMessage>? second = null;
        try
        {
            await firstStart.WaitAsync(Timeout);
            await fixture.Proxy.CompactionFor("first").WaitAsync(Timeout);
            var secondStart = fixture.Start("second");
            second = secondStart.Unwrap();
            await secondStart.WaitAsync(Timeout);
            // The invocation has returned after synchronous progress; it may be waiting at a gate.
            Assert.AreEqual(0, fixture.Proxy.Overlaps, "A handler entered during compaction.");
            fixture.Proxy.ReleaseCompaction();
            await fixture.Proxy.Entered("first").WaitAsync(Timeout);
            await Task.Run(() => fixture.Proxy.Release("first")).WaitAsync(Timeout);
            await fixture.Proxy.Entered("second").WaitAsync(Timeout);
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            Assert.AreEqual(0, fixture.Proxy.Overlaps, "A handler entered during compaction.");
        }
        finally
        {
            await Task.Run(fixture.Proxy.ReleaseAll).WaitAsync(Timeout);
            await DrainAsync(first, second);
        }
    }

    private static async Task DrainAsync(params Task?[] tasks)
    {
        var running = tasks.Where(t => t is not null).Cast<Task>().ToArray();
        if (running.Length > 0)
            await Task.WhenAll(running).WaitAsync(Timeout);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly FakeAgentHost host;
        public ControlledProxy Proxy { get; }

        private Fixture(ServiceProvider services, FakeAgentHost host, ControlledProxy proxy)
            => (this.services, this.host, Proxy) = (services, host, proxy);

        public static async Task<Fixture> CreateAsync(int staleAfterMinutes = 60)
        {
            var host = new FakeAgentHost("owner:agent");
            var services = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IFabrCoreChatClientService>(new FakeChatClientService(FakeChatClient.WithTextResponse("unused")))
                .BuildServiceProvider();
            var proxy = new ControlledProxy(new AgentConfiguration
            {
                Handle = host.GetHandle(), AgentType = "concurrency-test", Models = "default",
                Args = new Dictionary<string, string>
                {
                    ["_CompactionMaxContextTokens"] = "1000",
                    ["_CompactionThreshold"] = "0.5",
                    ["_CompactionStaleAfterMinutes"] = staleAfterMinutes.ToString()
                }
            }, services, host);
            await proxy.OnInitialize().WaitAsync(Timeout);
            return new Fixture(services, host, proxy);
        }

        public void SeedOversizedHistory() => host.Threads["main"] =
        [new StoredChatMessage { Role = "user", ContentsJson = JsonSerializer.Serialize(
            new List<AIContent> { new TextContent(new string('x', 10000)) }, AgentAbstractionsJsonUtilities.DefaultOptions) }];

        public Task<Task<AgentMessage>> Start(string name) => Task.Factory.StartNew(
            () => Send(name), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        private Task<AgentMessage> Send(string name)
        {
            var previous = Dispatch.Value;
            Dispatch.Value = name;
            try
            {
                return ((IFabrCoreAgentProxy)Proxy).InternalOnMessage(new AgentMessage
                { FromHandle = "owner", ToHandle = "owner:agent", Message = name });
            }
            finally { Dispatch.Value = previous; }
        }

        public void Dispose() => services.Dispose();
    }

    private sealed class ControlledProxy(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
        : FabrCoreAgentProxy(config, services, host)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> entered = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> exited = new();
        private readonly ConcurrentDictionary<string, int> exitThreads = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> compactions = new();
        private readonly TaskCompletionSource releaseCompaction = NewSignal();
        private readonly object sync = new();
        private int activeHandlers;
        private int activeCompactions;
        private int overlaps;
        private int compactionCount;

        public bool BlockCompaction { get; set; }
        public int ActiveHandlers { get { lock (sync) return activeHandlers; } }
        public int Overlaps { get { lock (sync) return overlaps; } }
        public int CompactionCount { get { lock (sync) return compactionCount; } }
        public Task Entered(string name) => Signal(entered, name).Task;
        public Task Exited(string name) => Signal(exited, name).Task;
        public int ExitThread(string name) => exitThreads[name];
        public Task CompactionFor(string name) => Signal(compactions, name).Task;
        public void Release(string name) => releases.GetOrAdd(name, _ => new TaskCompletionSource()).TrySetResult();
        public void ReleaseCompaction() => releaseCompaction.TrySetResult();
        public void ReleaseAll()
        {
            Release("first");
            Release("second");
            Release("third");
            ReleaseCompaction();
        }

        public override async Task OnInitialize() => await CreateChatClientAgent("default", "main");

        public override async Task<AgentMessage> OnMessage(AgentMessage message)
        {
            var name = message.Message!;
            lock (sync)
            {
                activeHandlers++;
                if (activeCompactions > 0) overlaps++;
            }
            Signal(entered, name).TrySetResult();
            try { await releases.GetOrAdd(name, _ => new TaskCompletionSource()).Task; }
            finally
            {
                lock (sync) activeHandlers--;
                exitThreads[name] = Environment.CurrentManagedThreadId;
                Signal(exited, name).TrySetResult();
            }
            return message.Response();
        }

        public override async Task<CompactionResult?> OnCompaction(
            FabrCoreChatHistoryProvider provider, CompactionConfig config, int estimatedTokens = 0)
        {
            lock (sync)
            {
                activeCompactions++;
                compactionCount++;
                if (activeHandlers > 0) overlaps++;
            }
            Signal(compactions, Dispatch.Value ?? "unknown").TrySetResult();
            try
            {
                if (BlockCompaction) await releaseCompaction.Task;
                return null;
            }
            finally { lock (sync) activeCompactions--; }
        }

        private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> signals, string name)
            => signals.GetOrAdd(name, _ => NewSignal());
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
