using FabrCore.Core;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using FabrCore.Host.Grains;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class WebSocketDeliveryGrainTests
{
    [TestMethod]
    public async Task AppendReplayAndAck_AreOrderedAndMonotonic()
    {
        var fixture = Create();
        var first = await fixture.Grain.RegisterClient("desktop", null);
        Assert.AreEqual(0, first.CurrentSequence);

        var one = await fixture.Grain.Append(new AgentMessage { Id = "one" });
        var two = await fixture.Grain.Append(new AgentMessage { Id = "two" });
        Assert.AreEqual(1, one!.Sequence);
        Assert.AreEqual(2, two!.Sequence);

        await fixture.Grain.MarkDelivered("desktop", 2);
        await fixture.Grain.Acknowledge("desktop", 1);
        Assert.AreEqual(1, fixture.State.State.Clients["desktop"].AcknowledgedSequence);
        CollectionAssert.AreEqual(new long[] { 2 }, fixture.State.State.Deliveries.Select(x => x.Sequence).ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Grain.Acknowledge("desktop", 3));
    }

    [TestMethod]
    public async Task Reconnect_ReplaysDuplicatesAfterLowerCheckpoint()
    {
        var fixture = Create();
        await fixture.Grain.RegisterClient("desktop", null);
        await fixture.Grain.Append(new AgentMessage { Id = "one" });
        await fixture.Grain.Append(new AgentMessage { Id = "two" });
        await fixture.Grain.MarkDelivered("desktop", 2);

        var replay = await fixture.Grain.RegisterClient("desktop", 0);

        CollectionAssert.AreEqual(new long[] { 1, 2 }, replay.Replay.Select(x => x.Sequence).ToArray());
        Assert.IsNull(replay.GapAfterSequence);
    }

    [TestMethod]
    public async Task NewClient_StartsAtTailAndMultipleClientsControlPruning()
    {
        var fixture = Create();
        await fixture.Grain.RegisterClient("a", null);
        await fixture.Grain.Append(new AgentMessage { Id = "one" });
        await fixture.Grain.MarkDelivered("a", 1);

        var newClient = await fixture.Grain.RegisterClient("b", 0);
        Assert.AreEqual(0, newClient.Replay.Count);

        await fixture.Grain.Acknowledge("a", 1);
        Assert.AreEqual(0, fixture.State.State.Deliveries.Count);
    }

    [TestMethod]
    public async Task CapacityCreatesGapAndStateSurvivesReactivation()
    {
        var fixture = Create(maxDeliveries: 2);
        await fixture.Grain.RegisterClient("desktop", null);
        await fixture.Grain.Append(new AgentMessage { Id = "one" });
        await fixture.Grain.Append(new AgentMessage { Id = "two" });
        await fixture.Grain.Append(new AgentMessage { Id = "three" });

        var reactivated = new WebSocketDeliveryGrain(fixture.State, Options.Create(fixture.Options), fixture.Clock);
        var registration = await reactivated.RegisterClient("desktop", 0);

        Assert.AreEqual(0, registration.GapAfterSequence);
        Assert.AreEqual(2, registration.OldestAvailableSequence);
        CollectionAssert.AreEqual(new long[] { 2, 3 }, registration.Replay.Select(x => x.Sequence).ToArray());
    }

    [TestMethod]
    public async Task InactiveClientsExpireAndStopRecording()
    {
        var fixture = Create(inactiveExpiration: TimeSpan.FromMinutes(1));
        await fixture.Grain.RegisterClient("desktop", null);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        var delivery = await fixture.Grain.Append(new AgentMessage { Id = "late" });

        Assert.IsNull(delivery);
        Assert.AreEqual(0, fixture.State.State.Clients.Count);
    }

    [TestMethod]
    public async Task TicketRedemption_IsSingleUseAndPersistsAcrossActivation()
    {
        var state = new FakePersistentState<FabrCoreWebSocketTicketState>(new());
        var issued = DateTimeOffset.Parse("2026-08-06T12:00:00Z");
        var firstActivation = new WebSocketTicketRegistryGrain(state);
        await firstActivation.Store("hash", new FabrCoreWebSocketTicketEntry
        {
            PrincipalHandle = "alice",
            IssuedAt = issued,
            ExpiresAt = issued.AddSeconds(30),
        }, 10);

        var secondActivation = new WebSocketTicketRegistryGrain(state);
        Assert.AreEqual("alice", await secondActivation.Redeem("hash", issued.AddSeconds(1)));
        Assert.IsNull(await secondActivation.Redeem("hash", issued.AddSeconds(2)));
    }

    [TestMethod]
    public async Task ExpiredTicketCannotBeRedeemed()
    {
        var state = new FakePersistentState<FabrCoreWebSocketTicketState>(new());
        var grain = new WebSocketTicketRegistryGrain(state);
        var issued = DateTimeOffset.Parse("2026-08-06T12:00:00Z");
        await grain.Store("hash", new FabrCoreWebSocketTicketEntry
        {
            PrincipalHandle = "alice",
            IssuedAt = issued,
            ExpiresAt = issued.AddSeconds(30),
        }, 10);

        Assert.IsNull(await grain.Redeem("hash", issued.AddSeconds(31)));
    }

    private static Fixture Create(int maxDeliveries = 10_000, TimeSpan? inactiveExpiration = null)
    {
        var state = new FakePersistentState<FabrCoreWebSocketDeliveryState>(new());
        var options = new FabrCoreWebSocketOptions
        {
            MaxDeliveriesPerPrincipal = maxDeliveries,
            InactiveClientExpiration = inactiveExpiration ?? TimeSpan.FromHours(24),
        };
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
        return new Fixture(new WebSocketDeliveryGrain(state, Options.Create(options), clock), state, options, clock);
    }

    private sealed record Fixture(
        WebSocketDeliveryGrain Grain,
        FakePersistentState<FabrCoreWebSocketDeliveryState> State,
        FabrCoreWebSocketOptions Options,
        ManualTimeProvider Clock);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class FakePersistentState<T>(T initial) : IPersistentState<T>
    {
        public T State { get; set; } = initial;
        public string Etag { get; set; } = string.Empty;
        public bool RecordExists { get; set; } = true;
        public Task ClearStateAsync() { State = default!; return Task.CompletedTask; }
        public Task WriteStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
    }
}
