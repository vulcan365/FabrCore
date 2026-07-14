# Principal message relays

Principal message relays let an agent send a message when its user has no live FabrCore observer.
The feature is provider-neutral:

```text
agent -> principal grain -> durable outbox -> provider relay -> external channel
                                      ^             |
                                      +-- completion+
```

Core persists the message before resolving a provider, chooses an endpoint, leases one ordered
outbox entry at a time, and retains it until the relay reports success, retry, endpoint
unavailability, or permanent failure. Delivery is durable at-least-once. A provider accepting a
request immediately before a process failure can receive the same delivery again, so provider
implementations should use `PrincipalMessageDelivery.DeliveryId` as an idempotency key when their
API supports one.

## Agent API

Agents do not depend on a provider package:

```csharp
await SendToUserAsync("Report ready");

await SendToUserAsync(
    "Your verification code is 123456",
    messageType: "verification.code",
    target: new PrincipalDeliveryTarget("sms", "verified-phone-1"));

await SendToUserAsync(new AgentMessage
{
    MessageType = "report.ready",
    DataType = "application/vnd.contoso.report+json",
    Data = JsonSerializer.SerializeToUtf8Bytes(report)
});
```

Without a target, FabrCore asks every installed relay and selects the eligible endpoint with the
most recent `LastActiveUtc`. With a target, only the named channel is considered and the provider
owns interpretation of `EndpointId`. A live principal observer takes precedence for newly arriving
messages; relay resolution occurs only when no observer is present.

## Provider lifecycle

1. Capture a verified endpoint during an inbound interaction, device registration, profile
   update, or administrative provisioning.
2. Store versioned provider metadata through `IPrincipalContextStore`, under a namespaced key such
   as `sms:endpoints:v1` or `webpush:subscriptions:v1`. Never store credentials or access tokens.
3. Implement `IPrincipalMessageRelay.ResolveAsync`. Return `NotApplicable` for unsupported message
   shapes, `Unavailable` when the channel applies but no endpoint can be used, and `Available` with
   the provider channel, endpoint id, and last-active timestamp.
4. Implement `TryEnqueueAsync` with a bounded in-process queue. It must return promptly and return
   `false` when full. Never wait for provider network I/O in this method.
5. From the worker, call `IPrincipalMessageDeliveryCompletion.CompleteAsync` with `Delivered`,
   `RetryableFailure`, `EndpointUnavailable`, or `PermanentFailure`.
6. Register the relay with `services.AddPrincipalMessageRelay<TRelay>()`.

Principal context is limited to 64 entries, 128 characters per key, 128 KiB per value, and 256 KiB
total. Store endpoint ids, eligibility, timestamps, and provider routing metadata there. Keep API
keys, signing secrets, OAuth credentials, and access tokens in normal DI-backed secret storage.

## Dependency-free webhook sample

This sample uses only FabrCore contracts, `HttpClient`, and the .NET runtime. It demonstrates
endpoint registration, resolution, bounded enqueueing, completion reporting, retry classification,
and DI registration. Production code should additionally authenticate callbacks, validate allowed
hosts during administrative provisioning, emit provider metrics, and include `DeliveryId` in a
provider-supported idempotency header.

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core;
using FabrCore.Host;
using Microsoft.Extensions.DependencyInjection;

public sealed record WebhookEndpoint(
    string EndpointId,
    string Url,
    bool Eligible,
    DateTimeOffset LastActiveUtc);

public sealed class WebhookRelay : IPrincipalMessageRelay, IAsyncDisposable
{
    private const string ContextKey = "webhook:endpoints:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IPrincipalContextStore _context;
    private readonly IPrincipalMessageDeliveryCompletion _completion;
    private readonly IHttpClientFactory _httpClients;
    private readonly Channel<PrincipalMessageDelivery> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    public WebhookRelay(
        IPrincipalContextStore context,
        IPrincipalMessageDeliveryCompletion completion,
        IHttpClientFactory httpClients)
    {
        _context = context;
        _completion = completion;
        _httpClients = httpClients;
        _queue = Channel.CreateBounded<PrincipalMessageDelivery>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => RunAsync(_stopping.Token));
    }

    public string Channel => "webhook";

    // Call from a verified administrative provisioning flow. The URL is metadata;
    // callback credentials belong in a secret store keyed by EndpointId.
    public async Task RegisterEndpointAsync(
        string principalHandle,
        string endpointId,
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        if (callback.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook endpoints must use HTTPS.", nameof(callback));

        var endpoints = await ReadEndpointsAsync(principalHandle, cancellationToken);
        endpoints.RemoveAll(e => e.EndpointId == endpointId);
        endpoints.Add(new WebhookEndpoint(endpointId, callback.ToString(), true, DateTimeOffset.UtcNow));
        await _context.SetAsync(
            principalHandle,
            ContextKey,
            JsonSerializer.Serialize(endpoints, JsonOptions),
            cancellationToken);
    }

    public ValueTask<PrincipalMessageRelayResolution> ResolveAsync(
        string principalHandle,
        AgentMessage message,
        PrincipalDeliveryTarget? target,
        IReadOnlyDictionary<string, string> principalContext,
        CancellationToken cancellationToken = default)
    {
        if (target?.Channel is { } channel &&
            !channel.Equals(Channel, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());

        if (message.IsSystemMessage ||
            (string.IsNullOrWhiteSpace(message.Message) && message.Data is not { Length: > 0 }))
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());

        principalContext.TryGetValue(ContextKey, out var json);
        var endpoints = DeserializeEndpoints(json);
        var endpoint = target?.EndpointId is { Length: > 0 } endpointId
            ? endpoints.FirstOrDefault(e => e.Eligible && e.EndpointId == endpointId)
            : endpoints.Where(e => e.Eligible).MaxBy(e => e.LastActiveUtc);

        return endpoint is null
            ? ValueTask.FromResult(PrincipalMessageRelayResolution.Unavailable(Channel))
            : ValueTask.FromResult(PrincipalMessageRelayResolution.Available(
                Channel, endpoint.EndpointId, endpoint.LastActiveUtc));
    }

    // TryWrite never waits and never drops an older item. False tells Core to keep
    // the durable entry and try again after recovery.
    public ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_queue.Writer.TryWrite(delivery));

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        try { await _worker; } catch (OperationCanceledException) { }
        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var delivery in _queue.Reader.ReadAllAsync(cancellationToken))
            await DeliverAsync(delivery, cancellationToken);
    }

    private async Task DeliverAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        delivery.PrincipalContext.TryGetValue(ContextKey, out var json);
        var endpoint = DeserializeEndpoints(json).FirstOrDefault(e =>
            e.Eligible && e.EndpointId == delivery.EndpointId);
        if (endpoint is null)
        {
            await CompleteAsync(delivery, PrincipalMessageDeliveryOutcome.EndpointUnavailable());
            return;
        }

        try
        {
            var client = _httpClients.CreateClient("principal-webhook");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
            {
                Content = JsonContent.Create(new
                {
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    delivery.Message
                })
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.DeliveryId);
            using var response = await client.SendAsync(request, cancellationToken);

            var status = (int)response.StatusCode;
            var outcome = status is >= 200 and < 300
                ? PrincipalMessageDeliveryOutcome.Delivered()
                : response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? PrincipalMessageDeliveryOutcome.EndpointUnavailable()
                    : response.StatusCode == HttpStatusCode.TooManyRequests
                        ? PrincipalMessageDeliveryOutcome.RetryableFailure(response.Headers.RetryAfter?.Delta)
                        : status >= 500
                            ? PrincipalMessageDeliveryOutcome.RetryableFailure()
                            : PrincipalMessageDeliveryOutcome.PermanentFailure(
                                $"Webhook returned HTTP {status}.");
            await CompleteAsync(delivery, outcome);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await CompleteAsync(
                delivery,
                PrincipalMessageDeliveryOutcome.RetryableFailure(error: ex.Message));
        }
    }

    private Task CompleteAsync(
        PrincipalMessageDelivery delivery,
        PrincipalMessageDeliveryOutcome outcome) =>
        _completion.CompleteAsync(
            delivery.PrincipalHandle, delivery.DeliveryId, outcome, CancellationToken.None);

    private async Task<List<WebhookEndpoint>> ReadEndpointsAsync(
        string principalHandle,
        CancellationToken cancellationToken) =>
        DeserializeEndpoints(await _context.GetAsync(principalHandle, ContextKey, cancellationToken));

    private static List<WebhookEndpoint> DeserializeEndpoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<WebhookEndpoint>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
}

public static class WebhookRelayServiceExtensions
{
    public static IServiceCollection AddPrincipalWebhookRelay(this IServiceCollection services)
    {
        services.AddHttpClient("principal-webhook", client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        services.AddPrincipalMessageRelay<WebhookRelay>();
        return services;
    }
}
```

The same shape applies to SMS, email, APNs/FCM, web push, Slack, and other packages. Only endpoint
metadata, message mapping, provider I/O, and failure classification change; agents and
`PrincipalGrain` do not.

## Operational behavior

- Outbox leases last two minutes. A one-minute Orleans reminder recovers expired leases after a
  crash or restart.
- Delivery entries expire after 24 hours.
- At most 100 dead letters are retained for seven days. Version 1 exposes logs and metrics, but no
  public replay API.
- Ordering is preserved per principal. Unsupported control messages remain pending but do not
  block later messages that another relay can deliver.
- A relay queue returning `false`, throwing while resolving, or disappearing during deployment
  does not discard durable work.
- The metrics meter `FabrCore.Host.PrincipalGrain` emits
  `fabrcore.principal.delivery.operations`, tagged only with bounded `status` and `channel` values.
