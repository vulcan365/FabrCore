using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using FabrCore.Client.WebSocket;
using FabrCore.Core;
using FabrCore.Core.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FabrCore.Surface.WebSocket.Tests;

[TestClass]
public sealed class SurfaceWebSocketPrincipalContextTests
{
    private const string Principal = "admin-1";
    private const string Agent = "admin-1:helper";

    [TestMethod]
    public async Task NoHost_SaysNotConfigured()
    {
        await using var harness = new Harness(hostUrl: null);

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() => harness.Factory.GetOrCreateAsync(Principal));

        Assert.AreEqual(SurfaceConnectionProblem.NotConfigured, problem.Problem);
    }

    [TestMethod]
    public async Task SlowConnect_SaysUnreachable()
    {
        await using var harness = new Harness(connectTimeout: TimeSpan.FromMilliseconds(50));
        harness.Socket.Connect = ct => Task.Delay(Timeout.Infinite, ct);

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() => harness.Factory.GetOrCreateAsync(Principal));

        Assert.AreEqual(SurfaceConnectionProblem.Unreachable, problem.Problem);
        Assert.IsTrue(harness.Socket.Disposed);
    }

    [TestMethod]
    public async Task RefusedConnect_SaysRefused()
    {
        await using var harness = new Harness();
        harness.Socket.Connect = _ => throw new FabrCoreWebSocketException("refused", "Refused (401).", retryable: false);

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() => harness.Factory.GetOrCreateAsync(Principal));

        Assert.AreEqual(SurfaceConnectionProblem.Refused, problem.Problem);
    }

    [TestMethod]
    public async Task Factory_SharesOneContextPerPrincipal_ActingForIt()
    {
        await using var harness = new Harness();

        var first = await harness.Factory.GetOrCreateAsync(Principal);
        var second = await harness.Factory.GetOrCreateAsync(Principal);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, harness.Opened);
        Assert.AreEqual(Principal, harness.SocketOptions!.ClientId);
        Assert.AreEqual("secret", harness.Http!.DefaultRequestHeaders.GetValues("x-api-key").Single());
        Assert.AreEqual(Principal, harness.Http.DefaultRequestHeaders.GetValues("x-user-handle").Single());
    }

    [TestMethod]
    public async Task Deliveries_AreRaisedAndAcknowledged()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        var received = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AgentMessageReceived += (_, message) => received.TrySetResult(message);

        harness.Socket.Deliver(new AgentMessage { FromHandle = Agent, Message = "Hello" });

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Hello", message.Message);
        await WaitUntilAsync(() => harness.Socket.Acks.Contains(1));
    }

    [TestMethod]
    public async Task Silence_PastTheQuietLimit_EndsTheWaitWithAnError()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        var received = new ConcurrentQueue<AgentMessage>();
        context.AgentMessageReceived += (_, message) => received.Enqueue(message);

        await context.SendMessage(new AgentMessage { ToHandle = "helper", Message = "Anyone?", Kind = MessageKind.Request });
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        harness.Context.CheckQuiet();
        Assert.IsEmpty(received);

        harness.Time.Advance(TimeSpan.FromMinutes(2));
        harness.Context.CheckQuiet();

        var error = received.Single();
        Assert.AreEqual(SystemMessageTypes.Error, error.MessageType);
        Assert.AreEqual(Agent, error.FromHandle);
        Assert.IsTrue(error.Args!.ContainsKey(SurfaceWebSocketPrincipalContext.NoAnswerArg));

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Context.CheckQuiet();
        Assert.HasCount(1, received);
    }

    [TestMethod]
    public async Task Progress_KeepsTheWaitAlive_AndAnAnswerEndsIt()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        var received = new ConcurrentQueue<AgentMessage>();
        context.AgentMessageReceived += (_, message) => received.Enqueue(message);

        await context.SendMessage(new AgentMessage { ToHandle = "helper", Message = "Look into it", Kind = MessageKind.Request });
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        harness.Context.Receive(new AgentMessage { FromHandle = Agent, MessageType = SystemMessageTypes.Status, Message = "Reading" });
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        harness.Context.CheckQuiet();
        Assert.HasCount(1, received);

        harness.Context.Receive(new AgentMessage { FromHandle = Agent, Message = "Done" });
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        harness.Context.CheckQuiet();
        Assert.HasCount(2, received);
        Assert.IsFalse(received.Any(message => message.MessageType == SystemMessageTypes.Error));
    }

    [TestMethod]
    public async Task OneWayMessages_AreNotWaitedOn()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        var received = new ConcurrentQueue<AgentMessage>();
        context.AgentMessageReceived += (_, message) => received.Enqueue(message);

        await context.SendMessage(new AgentMessage { ToHandle = "system:shared", Kind = MessageKind.OneWay, MessageType = "new-conversation" });
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        harness.Context.CheckQuiet();

        Assert.IsEmpty(received);
        Assert.AreEqual(Principal, harness.Socket.Sent.Single().FromHandle);
    }

    [TestMethod]
    public async Task CreateAgent_PostsToTheHost_AndRestoresItWhenTheHostForgetsIt()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        harness.Socket.Health.Enqueue(Healthy(Agent));
        harness.Socket.Health.Enqueue(NotSetUp(Agent));
        harness.Socket.Health.Enqueue(Healthy(Agent));

        var created = await context.CreateAgent(new AgentConfiguration { Handle = "helper", AgentType = "helper-agent" });
        Assert.IsTrue(created.IsConfigured);
        Assert.AreEqual(1, harness.Creates);

        var health = await context.GetAgentHealth("helper");

        Assert.IsTrue(health.IsConfigured);
        Assert.AreEqual(2, harness.Creates);
        Assert.AreEqual("/fabrcoreapi/Agent/create", harness.LastCreate!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task AgentsItDidNotCreate_AreNeverCreated()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        harness.Socket.Health.Enqueue(NotSetUp("system:shared"));

        var health = await context.GetAgentHealth("system:shared");

        Assert.IsFalse(health.IsConfigured);
        Assert.AreEqual(0, harness.Creates);
    }

    [TestMethod]
    public async Task CreateAgent_ForATypeTheHostLacks_SaysAgentMissing()
    {
        await using var harness = new Harness(createResult: """{"successCount":0,"results":[{"error":"Unknown type"}]}""");
        var context = await harness.Factory.GetOrCreateAsync(Principal);

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() =>
            context.CreateAgent(new AgentConfiguration { Handle = "helper", AgentType = "helper-agent" }));

        Assert.AreEqual(SurfaceConnectionProblem.AgentMissing, problem.Problem);
    }

    [TestMethod]
    public async Task CreateAgent_WithARefusedKey_SaysRefused()
    {
        await using var harness = new Harness(createStatus: HttpStatusCode.Unauthorized);
        var context = await harness.Factory.GetOrCreateAsync(Principal);

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() =>
            context.CreateAgent(new AgentConfiguration { Handle = "helper", AgentType = "helper-agent" }));

        Assert.AreEqual(SurfaceConnectionProblem.Refused, problem.Problem);
    }

    [TestMethod]
    public async Task ASendThatDoesNotReachTheHost_SaysUnreachable()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        harness.Socket.FailSends = true;

        var problem = await Assert.ThrowsExactlyAsync<SurfaceConnectionException>(() =>
            context.SendMessage(new AgentMessage { ToHandle = "helper", Message = "Hi" }));

        Assert.AreEqual(SurfaceConnectionProblem.Unreachable, problem.Problem);
    }

    [TestMethod]
    public async Task AConnectionThatStops_IsOpenedAgainOnTheNextCall()
    {
        await using var harness = new Harness();
        var context = await harness.Factory.GetOrCreateAsync(Principal);
        var first = harness.Socket;
        harness.NextSocket = new FakeSocket();

        first.Deliveries.Writer.Complete(new IOException("gone"));
        await WaitUntilAsync(() => first.Disposed);
        await context.SendMessage(new AgentMessage { ToHandle = "helper", Message = "Still there?", Kind = MessageKind.OneWay });

        Assert.AreEqual(2, harness.Opened);
        Assert.AreEqual("Still there?", harness.NextSocket.Sent.Single().Message);
    }

    [TestMethod]
    [DataRow("PromptTooLarge", "grown too long")]
    [DataRow(null, "Something went wrong while answering: boom")]
    public void Errors_AreSaidInPlainWords(string? reason, string expected)
    {
        var message = new AgentMessage { MessageType = SystemMessageTypes.Error, Message = "boom", Args = [] };
        if (reason is not null)
            message.Args["_fabrcore_run_stop_reason"] = reason;

        StringAssert.Contains(SurfaceWebSocketPrincipalContext.ErrorText(message), expected);
    }

    private static AgentHealthStatus Healthy(string handle)
        => new() { Handle = handle, State = HealthState.Healthy, Timestamp = DateTime.UtcNow, IsConfigured = true };

    private static AgentHealthStatus NotSetUp(string handle)
        => new() { Handle = handle, State = HealthState.NotConfigured, Timestamp = DateTime.UtcNow, IsConfigured = false };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail("Timed out waiting.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        public Harness(
            string? hostUrl = "https://host.example",
            TimeSpan? connectTimeout = null,
            string createResult = """{"successCount":1}""",
            HttpStatusCode createStatus = HttpStatusCode.OK)
        {
            var collection = new ServiceCollection();
            collection.AddHttpClient(SurfaceWebSocketExtensions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new Respond(request =>
                {
                    Creates++;
                    LastCreate = request;
                    return new HttpResponseMessage(createStatus) { Content = new StringContent(createResult) };
                }));
            services = collection.BuildServiceProvider();
            Factory = new SurfaceWebSocketPrincipalContextFactory(
                services.GetRequiredService<IHttpClientFactory>(),
                Options.Create(new SurfaceWebSocketOptions
                {
                    HostUrl = hostUrl is null ? null : new Uri(hostUrl),
                    ApiKey = "secret",
                    ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
                    QuietLimit = TimeSpan.FromMinutes(3)
                }),
                NullLoggerFactory.Instance,
                Time)
            {
                OpenSocket = (http, options) =>
                {
                    Opened++;
                    Http = http;
                    SocketOptions = options;
                    if (Opened > 1 && NextSocket is not null)
                        Socket = NextSocket;
                    return Socket;
                }
            };
        }

        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        public SurfaceWebSocketPrincipalContextFactory Factory { get; }
        public FakeSocket Socket { get; private set; } = new();
        public FakeSocket? NextSocket { get; set; }
        public int Opened { get; private set; }
        public int Creates { get; private set; }
        public HttpRequestMessage? LastCreate { get; private set; }
        public HttpClient? Http { get; private set; }
        public FabrCoreWebSocketClientOptions? SocketOptions { get; private set; }

        public SurfaceWebSocketPrincipalContext Context
            => (SurfaceWebSocketPrincipalContext)Factory.GetOrCreateAsync(Principal).GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    internal sealed class FakeSocket : IFabrCoreWebSocketClient
    {
        private long sequence;

        public Func<CancellationToken, Task> Connect { get; set; } = _ => Task.CompletedTask;
        public Channel<FabrCoreWebSocketDelivery> Deliveries { get; } = Channel.CreateUnbounded<FabrCoreWebSocketDelivery>();
        public ConcurrentQueue<AgentHealthStatus> Health { get; } = new();
        public List<AgentMessage> Sent { get; } = [];
        public ConcurrentBag<long> Acks { get; } = [];
        public bool FailSends { get; set; }
        public bool Disposed { get; private set; }
        public string ClientId => Principal;

#pragma warning disable CS0067
        public event EventHandler<FabrCoreWebSocketGapEventArgs>? ResyncRequired;
#pragma warning restore CS0067

        public void Deliver(AgentMessage message)
        {
            var next = Interlocked.Increment(ref sequence);
            Deliveries.Writer.TryWrite(new FabrCoreWebSocketDelivery(next, $"d{next}", message));
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Connect(cancellationToken);

        public Task<FabrCoreWebSocketAccepted> SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        {
            if (FailSends)
                throw new InvalidOperationException("The FabrCore WebSocket is not connected.");
            lock (Sent)
                Sent.Add(message);
            return Task.FromResult(new FabrCoreWebSocketAccepted());
        }

        public Task<AgentMessage> SendMessageAndReceiveAsync(AgentMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentMessage { FromHandle = message.ToHandle, Message = "ok" });

        public Task<FabrCoreWebSocketAccepted> SendEventAsync(EventMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(new FabrCoreWebSocketAccepted());

        public Task<AgentHealthStatus> ResetAgentAsync(string handle, CancellationToken cancellationToken = default)
            => Task.FromResult(Healthy(handle));

        public Task<AgentHealthStatus> GetAgentHealthAsync(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic, CancellationToken cancellationToken = default)
            => Task.FromResult(Health.TryDequeue(out var health) ? health : Healthy(handle));

        public Task<IReadOnlyList<TrackedAgentInfo>> GetTrackedAgentsAsync(bool activate = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackedAgentInfo>>([]);

        public Task<bool> IsAgentTrackedAsync(string handle, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AgentInfo>> GetSharedAgentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

        public IAsyncEnumerable<FabrCoreWebSocketDelivery> ReadDeliveriesAsync(CancellationToken cancellationToken = default)
            => Deliveries.Reader.ReadAllAsync(cancellationToken);

        public Task AcknowledgeAsync(long sequence, CancellationToken cancellationToken = default)
        {
            Acks.Add(sequence);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
