using FabrCore.Core;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class PrincipalMessageRelayTests
{
    [TestMethod]
    public async Task ResolveWithoutTarget_SelectsMostRecentlyActiveEndpointAcrossProviders()
    {
        var sms = Available("sms", "phone-1", DateTimeOffset.UtcNow.AddMinutes(-5));
        var webhook = Available("webhook", "hook-1", DateTimeOffset.UtcNow);
        var dispatcher = CreateDispatcher(sms, webhook);

        var result = await dispatcher.ResolveAsync("user", new AgentMessage { Message = "ready" },
            new Dictionary<string, string>());

        Assert.AreEqual(PrincipalMessageRelayResolutionStatus.Available, result.Status);
        Assert.AreEqual("webhook", result.Channel);
        Assert.AreEqual("hook-1", result.EndpointId);
    }

    [TestMethod]
    public async Task ExplicitTarget_OnlyInvokesRequestedRelayAndPreservesEndpoint()
    {
        var sms = Available("sms", "phone-1", DateTimeOffset.UtcNow);
        var webhook = Available("webhook", "hook-2", DateTimeOffset.UtcNow);
        var message = new AgentMessage
        {
            Message = "ready",
            DeliveryTarget = new PrincipalDeliveryTarget("webhook", "hook-requested")
        };

        var result = await CreateDispatcher(sms, webhook).ResolveAsync(
            "user", message, new Dictionary<string, string>());

        Assert.AreEqual("webhook", result.Channel);
        Assert.AreEqual(0, sms.ResolveCalls);
        Assert.AreEqual(1, webhook.ResolveCalls);
        Assert.AreEqual("hook-requested", webhook.LastTarget?.EndpointId);
    }

    [TestMethod]
    public async Task InapplicableProvider_DoesNotBlockEligibleLaterProvider()
    {
        var rejecting = new FakeRelay(
            "rejecting",
            PrincipalMessageRelayResolution.NotApplicable());
        var sms = Available("sms", "phone-1", DateTimeOffset.UtcNow);

        var result = await CreateDispatcher(rejecting, sms).ResolveAsync(
            "user", new AgentMessage { Message = "ready" }, new Dictionary<string, string>());

        Assert.AreEqual(PrincipalMessageRelayResolutionStatus.Available, result.Status);
        Assert.AreEqual("sms", result.Channel);
    }

    [TestMethod]
    public async Task NoRelays_LeavesMessageUnavailable()
    {
        var result = await CreateDispatcher().ResolveAsync(
            "user", new AgentMessage { Message = "ready" }, new Dictionary<string, string>());

        Assert.AreEqual(PrincipalMessageRelayResolutionStatus.Unavailable, result.Status);
    }

    [TestMethod]
    public async Task SaturatedRelay_ReturnsFalseWithoutDroppingIntoAnotherProvider()
    {
        var relay = Available("sms", "phone-1", DateTimeOffset.UtcNow);
        relay.AcceptEnqueue = false;

        var accepted = await CreateDispatcher(relay).TryEnqueueAsync(new PrincipalMessageDelivery
        {
            DeliveryId = "delivery-1",
            PrincipalHandle = "user",
            Message = new AgentMessage { Message = "ready" },
            Channel = "sms",
            EndpointId = "phone-1",
            PrincipalContext = new Dictionary<string, string>()
        });

        Assert.IsFalse(accepted);
        Assert.AreEqual(1, relay.EnqueueCalls);
    }

    [TestMethod]
    public void RelayRegistration_AllowsMultipleProviderPackages()
    {
        var services = new ServiceCollection();
        services.AddPrincipalMessageRelay<SmsRelay>();
        services.AddPrincipalMessageRelay<WebhookRelay>();
        using var provider = services.BuildServiceProvider();

        var relays = provider.GetServices<IPrincipalMessageRelay>().ToArray();

        CollectionAssert.AreEquivalent(new[] { "sms", "webhook" }, relays.Select(r => r.Channel).ToArray());
    }

    [TestMethod]
    public void ContextLimits_EnforceEntryKeyValueAndTotalBounds()
    {
        var options = new PrincipalContextOptions
        {
            MaxEntries = 2,
            MaxKeyLength = 5,
            MaxValueBytes = 4,
            MaxTotalBytes = 9
        };
        var context = new Dictionary<string, string>();

        Assert.IsTrue(PrincipalContextValues.Apply(context, "a", "1234", options));
        Assert.IsFalse(PrincipalContextValues.Apply(context, "a", "1234", options));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrincipalContextValues.Apply(context, "a", "12345", options));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PrincipalContextValues.Apply(context, "abcdef", "1", options));

        Assert.IsTrue(PrincipalContextValues.Apply(context, "b", "12", options));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PrincipalContextValues.Apply(context, "c", "1", options));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PrincipalContextValues.Apply(context, "b", "1234", options));

        Assert.IsTrue(PrincipalContextValues.Apply(context, "a", null, options));
        Assert.AreEqual(1, context.Count);
    }

    [TestMethod]
    public void StateMigration_DefaultsNewDeliveryCollectionsForOldState()
    {
        var converter = new PrincipalGrainStateSurrogateConverter();
        var state = converter.ConvertFromSurrogate(default);

        Assert.IsNotNull(state.TrackedAgents);
        Assert.IsNotNull(state.PendingMessages);
        Assert.IsNotNull(state.ContextValues);
        Assert.IsNotNull(state.DeliveryOutbox);
        Assert.IsNotNull(state.DeliveryDeadLetters);
    }

    [TestMethod]
    public void AgentMessageSerialization_PreservesGenericDeliveryTarget()
    {
        var converter = new AgentMessageSurrogateConverter();
        var original = new AgentMessage
        {
            Message = "ready",
            DeliveryTarget = new PrincipalDeliveryTarget("sms", "phone-1")
        };

        var roundTrip = converter.ConvertFromSurrogate(converter.ConvertToSurrogate(original));

        Assert.AreEqual("sms", roundTrip.DeliveryTarget?.Channel);
        Assert.AreEqual("phone-1", roundTrip.DeliveryTarget?.EndpointId);
    }

    [TestMethod]
    public void ObserverPrecedence_UsesObserverPathOnlyWhenAnObserverIsLive()
    {
        Assert.IsFalse(PrincipalDeliveryStateMachine.ShouldDeliverToObservers(0));
        Assert.IsTrue(PrincipalDeliveryStateMachine.ShouldDeliverToObservers(1));
        Assert.IsTrue(PrincipalDeliveryStateMachine.ShouldDeliverToObservers(3));
    }

    [TestMethod]
    public void ExpiredLease_IsRecoveredAfterPersistedStateRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new PrincipalGrainState();
        state.DeliveryOutbox.Add(new PrincipalDeliveryOutboxEntry
        {
            DeliveryId = "delivery-1",
            Message = new AgentMessage { Message = "ready" },
            Channel = "sms",
            EndpointId = "phone-1",
            CreatedUtc = now.AddMinutes(-5),
            AvailableAfterUtc = now.AddMinutes(-5),
            LeaseExpiresUtc = now.AddSeconds(-1)
        });
        var converter = new PrincipalGrainStateSurrogateConverter();
        var restored = converter.ConvertFromSurrogate(converter.ConvertToSurrogate(state));
        var entry = restored.DeliveryOutbox.Single();

        Assert.IsTrue(PrincipalDeliveryStateMachine.TryRecoverExpiredLease(entry, now));
        Assert.IsNull(entry.LeaseExpiresUtc);
        Assert.IsTrue(PrincipalDeliveryStateMachine.TryAcquireLease(
            entry, now, TimeSpan.FromMinutes(2)));
        Assert.AreEqual(now.AddMinutes(2), entry.LeaseExpiresUtc);
        Assert.IsFalse(PrincipalDeliveryStateMachine.TryAcquireLease(
            entry, now.AddMinutes(1), TimeSpan.FromMinutes(2)));
    }

    [TestMethod]
    public void RetryAndEndpointUnavailable_ApplyDistinctDurableTransitions()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new PrincipalDeliveryOptions();
        var retry = new PrincipalDeliveryOutboxEntry
        {
            CreatedUtc = now,
            LeaseExpiresUtc = now.AddMinutes(2)
        };

        PrincipalDeliveryStateMachine.ApplyRetryableFailure(
            retry,
            PrincipalMessageDeliveryOutcome.RetryableFailure(TimeSpan.FromMinutes(5), "temporary"),
            options,
            now);
        Assert.AreEqual(1, retry.AttemptCount);
        Assert.IsNull(retry.LeaseExpiresUtc);
        Assert.IsFalse(retry.WaitingForEndpointRefresh);
        Assert.AreEqual(now.AddMinutes(5), retry.AvailableAfterUtc);

        PrincipalDeliveryStateMachine.ApplyEndpointUnavailable(
            retry,
            PrincipalMessageDeliveryOutcome.EndpointUnavailable(error: "stale"),
            options,
            now);
        Assert.AreEqual(2, retry.AttemptCount);
        Assert.IsTrue(retry.WaitingForEndpointRefresh);
        Assert.AreEqual(retry.CreatedUtc + options.MaxDeliveryAge, retry.AvailableAfterUtc);
    }

    [TestMethod]
    public void DeadLetters_AreRetainedByAgeAndBoundedCount()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new PrincipalDeliveryOptions
        {
            MaxDeadLetters = 2,
            DeadLetterRetention = TimeSpan.FromDays(7)
        };
        var state = new PrincipalGrainState();
        state.DeliveryDeadLetters.Add(new PrincipalDeliveryDeadLetter
        {
            DeliveryId = "expired",
            DeadLetteredUtc = now.AddDays(-8)
        });

        foreach (var id in new[] { "one", "two", "three" })
        {
            var entry = new PrincipalDeliveryOutboxEntry
            {
                DeliveryId = id,
                Message = new AgentMessage { Message = id },
                Channel = "webhook",
                EndpointId = "endpoint",
                CreatedUtc = now
            };
            state.DeliveryOutbox.Add(entry);
            PrincipalDeliveryStateMachine.MoveToDeadLetters(
                state, entry, "permanent", options, now);
        }

        Assert.AreEqual(0, state.DeliveryOutbox.Count);
        CollectionAssert.AreEqual(
            new[] { "two", "three" },
            state.DeliveryDeadLetters.Select(item => item.DeliveryId).ToArray());
    }

    private static PrincipalMessageRelayDispatcher CreateDispatcher(params FakeRelay[] relays) =>
        new(relays, NullLogger<PrincipalMessageRelayDispatcher>.Instance);

    private static FakeRelay Available(
        string channel,
        string endpointId,
        DateTimeOffset lastActive) =>
        new(channel, PrincipalMessageRelayResolution.Available(channel, endpointId, lastActive));

    private sealed class FakeRelay(
        string channel,
        PrincipalMessageRelayResolution resolution) : IPrincipalMessageRelay
    {
        public string Channel { get; } = channel;
        public bool AcceptEnqueue { get; set; } = true;
        public int ResolveCalls { get; private set; }
        public int EnqueueCalls { get; private set; }
        public PrincipalDeliveryTarget? LastTarget { get; private set; }

        public ValueTask<PrincipalMessageRelayResolution> ResolveAsync(
            string principalHandle,
            AgentMessage message,
            PrincipalDeliveryTarget? target,
            IReadOnlyDictionary<string, string> principalContext,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            LastTarget = target;
            return ValueTask.FromResult(resolution);
        }

        public ValueTask<bool> TryEnqueueAsync(
            PrincipalMessageDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            return ValueTask.FromResult(AcceptEnqueue);
        }
    }

    private sealed class SmsRelay : NoOpRelay
    {
        public override string Channel => "sms";
    }

    private sealed class WebhookRelay : NoOpRelay
    {
        public override string Channel => "webhook";
    }

    private abstract class NoOpRelay : IPrincipalMessageRelay
    {
        public abstract string Channel { get; }

        public ValueTask<PrincipalMessageRelayResolution> ResolveAsync(
            string principalHandle,
            AgentMessage message,
            PrincipalDeliveryTarget? target,
            IReadOnlyDictionary<string, string> principalContext,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());

        public ValueTask<bool> TryEnqueueAsync(
            PrincipalMessageDelivery delivery,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }
}
