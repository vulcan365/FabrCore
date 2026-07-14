using System.Net;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotProactiveDeliveryTests
{
    [TestMethod]
    public async Task Relay_SelectsLatestEligibleEndpoint_AndHonorsExplicitEndpoint()
    {
        var older = Endpoint("older", DateTimeOffset.UtcNow.AddMinutes(-5));
        var latest = Endpoint("latest", DateTimeOffset.UtcNow);
        var disabled = Endpoint("disabled", DateTimeOffset.UtcNow.AddMinutes(5));
        disabled.Eligible = false;
        var context = RegistryContext(older, latest, disabled);
        var relay = new CopilotPrincipalMessageRelay(
            null!,
            NullLogger<CopilotPrincipalMessageRelay>.Instance);

        var automatic = await relay.ResolveAsync(
            "user",
            new AgentMessage { Message = "Report ready" },
            null,
            context);
        var explicitResult = await relay.ResolveAsync(
            "user",
            new AgentMessage { Message = "Report ready" },
            new PrincipalDeliveryTarget(Microsoft365CopilotDefaults.ChannelName, "older"),
            context);

        Assert.AreEqual("latest", automatic.EndpointId);
        Assert.AreEqual("older", explicitResult.EndpointId);
    }

    [TestMethod]
    public async Task Relay_RejectsSystemBlankForeignAndUnsupportedUiMessages()
    {
        var relay = new CopilotPrincipalMessageRelay(
            null!,
            NullLogger<CopilotPrincipalMessageRelay>.Instance);
        var context = RegistryContext(Endpoint("endpoint", DateTimeOffset.UtcNow));
        var messages = new[]
        {
            new AgentMessage { MessageType = SystemMessageTypes.Status, Message = "working" },
            new AgentMessage { Message = " " },
            new AgentMessage { MessageType = CopilotActivityMapper.UiActionMessageType, Message = "submit" }
        };

        foreach (var message in messages)
        {
            var result = await relay.ResolveAsync("user", message, null, context);
            Assert.AreEqual(PrincipalMessageRelayResolutionStatus.NotApplicable, result.Status);
        }

        var foreign = await relay.ResolveAsync(
            "user",
            new AgentMessage { Message = "ready" },
            new PrincipalDeliveryTarget("sms"),
            context);
        Assert.AreEqual(PrincipalMessageRelayResolutionStatus.NotApplicable, foreign.Status);
    }

    [TestMethod]
    public void ConversationRegistry_RoundTripsAnonymousAndAuthenticatedClaims()
    {
        var anonymousDelivery = Delivery(Endpoint("anonymous", DateTimeOffset.UtcNow));
        Assert.IsTrue(CopilotProactiveMessenger.TryBuildConversation(
            anonymousDelivery,
            out var anonymous,
            out var anonymousError), anonymousError);
        Assert.IsNotNull(anonymous);

        var authenticatedEndpoint = Endpoint("authenticated", DateTimeOffset.UtcNow);
        authenticatedEndpoint.Claims["tid"] = "tenant-1";
        authenticatedEndpoint.Claims["azp"] = "app-1";
        var authenticatedDelivery = Delivery(authenticatedEndpoint);

        Assert.IsTrue(CopilotProactiveMessenger.TryBuildConversation(
            authenticatedDelivery,
            out var authenticated,
            out var authenticatedError), authenticatedError);
        Assert.AreEqual("tenant-1", authenticated!.Identity.FindFirst("tid")?.Value);
        Assert.AreEqual("app-1", authenticated.Identity.FindFirst("azp")?.Value);
    }

    [TestMethod]
    public void ActivityMapping_AcceptsTextAndValidCards_RejectsMalformedCards()
    {
        Assert.IsTrue(CopilotProactiveMessenger.TryBuildActivity(
            new AgentMessage { Message = "Report ready" },
            out var text,
            out _));
        Assert.AreEqual("Report ready", text!.Text);

        var card = new AgentMessage
        {
            MessageType = CopilotActivityMapper.UiRenderMessageType,
            DataType = CopilotActivityMapper.SurfaceAdaptiveCardDataType,
            Data = Encoding.UTF8.GetBytes("""{"type":"AdaptiveCard","version":"1.5","body":[]}""")
        };
        Assert.IsTrue(CopilotProactiveMessenger.TryBuildActivity(card, out var cardActivity, out _));
        Assert.AreEqual(1, cardActivity!.Attachments?.Count);

        card.Data = Encoding.UTF8.GetBytes("not-json");
        Assert.IsFalse(CopilotProactiveMessenger.TryBuildActivity(card, out _, out var error));
        StringAssert.Contains(error, "could not be mapped");
    }

    [TestMethod]
    public async Task Messenger_RetriesTransientFailuresThenReportsDelivered()
    {
        var sender = new FakeSender((attempt, _) =>
            attempt < 3
                ? Task.FromException(new HttpRequestException(
                    "temporary",
                    null,
                    HttpStatusCode.InternalServerError))
                : Task.CompletedTask);
        var completion = new FakeCompletion();
        await using var messenger = CreateMessenger(sender, completion);

        Assert.IsTrue(messenger.TryEnqueue(Delivery(Endpoint("endpoint", DateTimeOffset.UtcNow))));
        var outcome = await completion.Next.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(3, sender.Calls);
        Assert.AreEqual(PrincipalMessageDeliveryOutcomeKind.Delivered, outcome.Kind);
    }

    [TestMethod]
    public async Task Messenger_Permanent4xxDeadLettersWithoutProviderRetry()
    {
        var sender = new FakeSender((_, _) => Task.FromException(
            new HttpRequestException("bad request", null, HttpStatusCode.BadRequest)));
        var completion = new FakeCompletion();
        await using var messenger = CreateMessenger(sender, completion);

        Assert.IsTrue(messenger.TryEnqueue(Delivery(Endpoint("endpoint", DateTimeOffset.UtcNow))));
        var outcome = await completion.Next.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, sender.Calls);
        Assert.AreEqual(PrincipalMessageDeliveryOutcomeKind.PermanentFailure, outcome.Kind);
    }

    [TestMethod]
    public async Task Messenger_UsesBoundedQueueAndReturnsFalseWhenSaturated()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var completion = new FakeCompletion();
        await using var messenger = CreateMessenger(sender, completion, capacity: 1);

        Assert.IsTrue(messenger.TryEnqueue(Delivery(Endpoint("one", DateTimeOffset.UtcNow), "one")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(messenger.TryEnqueue(Delivery(Endpoint("two", DateTimeOffset.UtcNow), "two")));
        Assert.IsFalse(messenger.TryEnqueue(Delivery(Endpoint("three", DateTimeOffset.UtcNow), "three")));
    }

    private static CopilotProactiveMessenger CreateMessenger(
        FakeSender sender,
        FakeCompletion completion,
        int capacity = 4)
    {
        var options = new Microsoft365CopilotOptions();
        options.Proactive.Enabled = true;
        options.Proactive.WorkerShards = 1;
        options.Proactive.OutboundQueueCapacity = capacity;
        options.Proactive.MaxDeliveryAttempts = 3;
        options.Proactive.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        options.Proactive.SendTimeout = TimeSpan.FromSeconds(2);

        return new CopilotProactiveMessenger(
            sender,
            completion,
            new FakeContextWriter(),
            Options.Create(options),
            NullLogger<CopilotProactiveMessenger>.Instance);
    }

    private static PrincipalMessageDelivery Delivery(
        CopilotConversationEndpoint endpoint,
        string? deliveryId = null) => new()
    {
        DeliveryId = deliveryId ?? Guid.NewGuid().ToString("N"),
        PrincipalHandle = "user",
        Message = new AgentMessage { Message = "Report ready" },
        Channel = Microsoft365CopilotDefaults.ChannelName,
        EndpointId = endpoint.EndpointId,
        PrincipalContext = RegistryContext(endpoint)
    };

    private static Dictionary<string, string> RegistryContext(
        params CopilotConversationEndpoint[] endpoints) => new()
    {
        [Microsoft365CopilotDefaults.ProactiveEndpointsContextKey] =
            CopilotConversationContextWriter.SerializeRegistry(new CopilotEndpointRegistry
            {
                Endpoints = endpoints.ToList()
            })
    };

    private static CopilotConversationEndpoint Endpoint(
        string endpointId,
        DateTimeOffset lastActive) => new()
    {
        EndpointId = endpointId,
        Eligible = true,
        ConversationType = "personal",
        LastActiveUtc = lastActive,
        Claims = new Dictionary<string, string>(),
        ConversationReferenceJson = JsonSerializer.Serialize(
            new ConversationReference
            {
                ChannelId = Channels.Msteams,
                ServiceUrl = "https://smba.trafficmanager.net/amer/",
                Conversation = new ConversationAccount
                {
                    Id = "conversation-" + endpointId,
                    ConversationType = "personal",
                    TenantId = "tenant-1"
                },
                Agent = new ChannelAccount { Id = "bot-1" },
                User = new ChannelAccount { Id = "user-1" }
            },
            ProtocolJsonSerializer.SerializationOptions)
    };

    private sealed class FakeSender(
        Func<int, CancellationToken, Task> send) : ICopilotProactiveActivitySender
    {
        public int Calls { get; private set; }

        public Task SendAsync(
            Conversation conversation,
            IActivity activity,
            CancellationToken cancellationToken)
        {
            Calls++;
            return send(Calls, cancellationToken);
        }
    }

    private sealed class FakeCompletion : IPrincipalMessageDeliveryCompletion
    {
        public TaskCompletionSource<PrincipalMessageDeliveryOutcome> Next { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CompleteAsync(
            string principalHandle,
            string deliveryId,
            PrincipalMessageDeliveryOutcome outcome,
            CancellationToken cancellationToken = default)
        {
            Next.TrySetResult(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeContextWriter : ICopilotConversationContextWriter
    {
        public Task<string?> TrackAsync(
            string principalHandle,
            ITurnContext turnContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task MarkUnavailableAsync(
            string principalHandle,
            string endpointId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
