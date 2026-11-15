using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Streams;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize]
public sealed class AgentChannelRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static WebApplication _app = null!;
    private static IFabrCoreAgentService Agents => _app.Services.GetRequiredService<IFabrCoreAgentService>();

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _app = DatabaseModeTests.BuildHost("Development", configureServices: services =>
            services.Configure<AgentGrainOptions>(options => options.HeartbeatInterval = TimeSpan.FromMilliseconds(150)));
        await _app.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [TestMethod]
    public async Task DifferentChannelsRunTogether_SameChannelIsBusy_AndReservationIsReleased()
    {
        var handle = await ConfigureAsync(true);
        var first = new ChannelTestTurn();
        var second = new ChannelTestTurn();
        var next = new ChannelTestTurn();
        try
        {
            var firstCall = Send(handle, "first", first, "alpha");
            await first.Entered.Task.WaitAsync(Timeout);
            var secondCall = Send(handle, "second", second, "beta");
            await second.Entered.Task.WaitAsync(Timeout);

            var busy = await Send(handle, "third", new ChannelTestTurn(), "alpha").WaitAsync(Timeout);
            Assert.AreEqual("busy", busy.Message);
            var health = await Agents.GetHealthAsync("owner", handle, HealthDetailLevel.Detailed);
            Assert.AreEqual(1L, health.BusyMessagesRouted);

            first.Release.TrySetResult();
            Assert.AreEqual("primary", (await firstCall.WaitAsync(Timeout)).Message);
            var nextCall = Send(handle, "fourth", next, "alpha");
            await next.Entered.Task.WaitAsync(Timeout);
            next.Release.TrySetResult();
            Assert.AreEqual("primary", (await nextCall.WaitAsync(Timeout)).Message);
            Assert.IsFalse(secondCall.IsCompleted, "Releasing one channel must not complete the other.");
            second.Release.TrySetResult();
            Assert.AreEqual("primary", (await secondCall.WaitAsync(Timeout)).Message);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            next.Release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(null, "")]
    [DataRow("", "  ")]
    [DataRow("  ", null)]
    public async Task NullAndBlankChannelsShareOneReservation(string? firstChannel, string? secondChannel)
    {
        var handle = await ConfigureAsync(true);
        var first = new ChannelTestTurn();
        try
        {
            var firstCall = Send(handle, "first", first, firstChannel);
            await first.Entered.Task.WaitAsync(Timeout);
            var busy = await Send(handle, "second", new ChannelTestTurn(), secondChannel).WaitAsync(Timeout);
            Assert.AreEqual("busy", busy.Message);
            first.Release.TrySetResult();
            Assert.AreEqual("primary", (await firstCall.WaitAsync(Timeout)).Message);
        }
        finally { first.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task DisabledChannelRoutingRetainsGlobalBusyBehavior()
    {
        var handle = await ConfigureAsync(false);
        var first = new ChannelTestTurn();
        try
        {
            var firstCall = Send(handle, "first", first, "alpha");
            await first.Entered.Task.WaitAsync(Timeout);
            var busy = await Send(handle, "second", new ChannelTestTurn(), "beta").WaitAsync(Timeout);
            Assert.AreEqual("busy", busy.Message);
            first.Release.TrySetResult();
            Assert.AreEqual("primary", (await firstCall.WaitAsync(Timeout)).Message);
        }
        finally { first.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task FailedPrimaryReleasesItsChannel()
    {
        var handle = await ConfigureAsync(true);
        var failing = new ChannelTestTurn { Fail = true };
        var next = new ChannelTestTurn();
        try
        {
            var failingCall = Send(handle, "first", failing, "alpha");
            await failing.Entered.Task.WaitAsync(Timeout);
            failing.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await failingCall.WaitAsync(Timeout));
            StringAssert.Contains(error.Message, "test failure");

            var nextCall = Send(handle, "second", next, "alpha");
            await next.Entered.Task.WaitAsync(Timeout);
            next.Release.TrySetResult();
            Assert.AreEqual("primary", (await nextCall.WaitAsync(Timeout)).Message);
        }
        finally
        {
            failing.Release.TrySetResult();
            next.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task HeartbeatsKeepConcurrentStatusesSeparate()
    {
        var handle = await ConfigureAsync(true);
        var client = _app.Services.GetRequiredService<IClusterClient>();
        var firstStatus = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStatus = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSubscription = await client.GetAgentChatStream("first-status").SubscribeAsync((message, _) =>
        {
            if (message.MessageType == SystemMessageTypes.Status) firstStatus.TrySetResult(message.Message);
            return Task.CompletedTask;
        });
        var secondSubscription = await client.GetAgentChatStream("second-status").SubscribeAsync((message, _) =>
        {
            if (message.MessageType == SystemMessageTypes.Status) secondStatus.TrySetResult(message.Message);
            return Task.CompletedTask;
        });
        var first = new ChannelTestTurn { Status = "alpha working" };
        var second = new ChannelTestTurn();
        try
        {
            var firstCall = Send(handle, "first-status", first, "alpha");
            await first.Entered.Task.WaitAsync(Timeout);
            var secondCall = Send(handle, "second-status", second, "beta");
            await second.Entered.Task.WaitAsync(Timeout);
            Assert.AreEqual("alpha working", await firstStatus.Task.WaitAsync(Timeout));
            Assert.AreEqual("Thinking..", await secondStatus.Task.WaitAsync(Timeout));
            first.Release.TrySetResult();
            await firstCall.WaitAsync(Timeout);
            Assert.IsFalse(secondCall.IsCompleted);
            second.Release.TrySetResult();
            await secondCall.WaitAsync(Timeout);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await firstSubscription.UnsubscribeAsync();
            await secondSubscription.UnsubscribeAsync();
        }
    }

    private static async Task<string> ConfigureAsync(bool channelRouting)
    {
        var handle = "routing-" + Guid.NewGuid().ToString("N");
        var health = await Agents.ConfigureAgentAsync("owner", new AgentConfiguration
        {
            Handle = handle,
            AgentType = "channel-routing-test",
            Args = new Dictionary<string, string> { ["_ChannelBusyRoutingEnabled"] = channelRouting.ToString() }
        });
        Assert.AreEqual(HealthState.Healthy, health.State, health.Message);
        return handle;
    }

    private static async Task<AgentMessage> Send(string handle, string sender, ChannelTestTurn turn, string? channel)
    {
        var id = Guid.NewGuid().ToString("N");
        ChannelRoutingTestAgent.Turns[id] = turn;
        try
        {
            return await Agents.SendAndReceiveMessageAsync(sender, "owner:" + handle, new AgentMessage { Message = id, Channel = channel });
        }
        finally { ChannelRoutingTestAgent.Turns.TryRemove(id, out _); }
    }
}

public sealed class ChannelTestTurn
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Status { get; init; }
    public bool Fail { get; init; }
}

[AgentAlias("channel-routing-test")]
public sealed class ChannelRoutingTestAgent(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
    : FabrCoreAgentProxy(config, services, host)
{
    public static ConcurrentDictionary<string, ChannelTestTurn> Turns { get; } = new();

    public override Task OnInitialize() => Task.CompletedTask;

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var turn = Turns[message.Message!];
        SetStatusMessage(turn.Status);
        turn.Entered.TrySetResult();
        await turn.Release.Task;
        if (turn.Fail) throw new InvalidOperationException("test failure");
        var response = message.Response();
        response.Message = "primary";
        return response;
    }

    public override Task<AgentMessage> OnMessageBusy(AgentMessage message)
    {
        var response = message.Response();
        response.Message = "busy";
        return Task.FromResult(response);
    }
}
