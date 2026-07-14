using System.Security.Claims;
using FabrCore.Core;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotConversationContextTests
{
    [TestMethod]
    public async Task Track_StoresStablePersonalEndpointsAndEnforcesMostRecentLimit()
    {
        var store = new MemoryContextStore();
        var options = new Microsoft365CopilotOptions();
        options.Proactive.Enabled = true;
        options.Proactive.MaxStoredEndpoints = 2;
        var writer = new CopilotConversationContextWriter(
            store,
            Options.Create(options),
            NullLogger<CopilotConversationContextWriter>.Instance);

        var firstId = await writer.TrackAsync("user", Context("conversation-1", "personal"));
        var sameId = await writer.TrackAsync("user", Context("conversation-1", "personal"));
        await writer.TrackAsync("user", Context("conversation-2", "personal"));
        await writer.TrackAsync("user", Context("conversation-3", "personal"));

        Assert.AreEqual(firstId, sameId);
        Assert.IsTrue(CopilotConversationContextWriter.TryParseRegistry(
            await store.GetAsync("user", Microsoft365CopilotDefaults.ProactiveEndpointsContextKey),
            out var registry));
        Assert.AreEqual(2, registry.Endpoints.Count);
        Assert.IsTrue(registry.Endpoints.Any(endpoint =>
            endpoint.ConversationReferenceJson.Contains("conversation-3", StringComparison.Ordinal)));
        Assert.IsFalse(registry.Endpoints.Any(endpoint =>
            endpoint.ConversationReferenceJson.Contains("conversation-1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Track_DefaultsToPersonalScopeAndStoresOnlyAllowlistedClaims()
    {
        var store = new MemoryContextStore();
        var options = new Microsoft365CopilotOptions();
        options.Proactive.Enabled = true;
        var writer = new CopilotConversationContextWriter(
            store,
            Options.Create(options),
            NullLogger<CopilotConversationContextWriter>.Instance);

        var ignored = await writer.TrackAsync("user", Context("channel-1", "channel"));
        var identity = new ClaimsIdentity(
            [
                new Claim("tid", "tenant-claim"),
                new Claim("azp", "app-claim"),
                new Claim("access_token", "must-not-be-stored")
            ],
            "test");
        var endpointId = await writer.TrackAsync(
            "user",
            Context("personal-1", "personal", identity));

        Assert.IsNull(ignored);
        Assert.IsNotNull(endpointId);
        Assert.IsTrue(CopilotConversationContextWriter.TryParseRegistry(
            await store.GetAsync("user", Microsoft365CopilotDefaults.ProactiveEndpointsContextKey),
            out var registry));
        var endpoint = registry.Endpoints.Single();
        Assert.AreEqual("tenant-claim", endpoint.Claims["tid"]);
        Assert.AreEqual("app-claim", endpoint.Claims["azp"]);
        Assert.IsFalse(endpoint.Claims.ContainsKey("access_token"));
    }

    [TestMethod]
    public async Task MarkUnavailable_DisablesEndpointUntilNextEligibleTurnRefreshesIt()
    {
        var store = new MemoryContextStore();
        var options = new Microsoft365CopilotOptions();
        options.Proactive.Enabled = true;
        var writer = new CopilotConversationContextWriter(
            store,
            Options.Create(options),
            NullLogger<CopilotConversationContextWriter>.Instance);
        var context = Context("personal-1", "personal");

        var endpointId = await writer.TrackAsync("user", context);
        await writer.MarkUnavailableAsync("user", endpointId!);
        Assert.IsTrue(CopilotConversationContextWriter.TryParseRegistry(
            await store.GetAsync("user", Microsoft365CopilotDefaults.ProactiveEndpointsContextKey),
            out var unavailable));
        Assert.IsFalse(unavailable.Endpoints.Single().Eligible);

        await writer.TrackAsync("user", context);
        Assert.IsTrue(CopilotConversationContextWriter.TryParseRegistry(
            await store.GetAsync("user", Microsoft365CopilotDefaults.ProactiveEndpointsContextKey),
            out var refreshed));
        Assert.IsTrue(refreshed.Endpoints.Single().Eligible);
    }

    private static FakeTurnContext Context(
        string conversationId,
        string conversationType,
        ClaimsIdentity? identity = null) => new(
        new Activity
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            ChannelId = Channels.Msteams,
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount
            {
                Id = "user-1",
                AadObjectId = "object-1",
                TenantId = "tenant-1"
            },
            Recipient = new ChannelAccount { Id = "bot-1" },
            Conversation = new ConversationAccount
            {
                Id = conversationId,
                ConversationType = conversationType,
                TenantId = "tenant-1"
            }
        },
        identity ?? new ClaimsIdentity());

    private sealed class MemoryContextStore : IPrincipalContextStore
    {
        private readonly Dictionary<string, Dictionary<string, string>> _values = new();

        public Task SetAsync(
            string principalHandle,
            string key,
            string? value,
            CancellationToken cancellationToken = default)
        {
            if (!_values.TryGetValue(principalHandle, out var context))
            {
                context = new Dictionary<string, string>();
                _values[principalHandle] = context;
            }

            if (value is null)
            {
                context.Remove(key);
            }
            else
            {
                context[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(
            string principalHandle,
            string key,
            CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(principalHandle, out var context);
            string? value = null;
            context?.TryGetValue(key, out value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(
            string principalHandle,
            CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(principalHandle, out var context);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                context is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(context));
        }
    }

    private sealed class FakeTurnContext(IActivity activity, ClaimsIdentity identity) : ITurnContext
    {
        public IActivity Activity { get; } = activity;
        public ClaimsIdentity Identity { get; } = identity;
        public IChannelAdapter Adapter => throw new NotSupportedException();
        public TurnContextStateCollection Services => throw new NotSupportedException();
        public TurnContextStateCollection StackState => throw new NotSupportedException();
        public IStreamingResponse StreamingResponse => throw new NotSupportedException();
        public bool Responded => false;
        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => throw new NotSupportedException();
        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => throw new NotSupportedException();
        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => throw new NotSupportedException();
        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> TraceActivityAsync(string name, object? value = null, string? valueType = null, string? label = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
