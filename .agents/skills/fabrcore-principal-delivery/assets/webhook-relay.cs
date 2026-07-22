using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core;
using FabrCore.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Example.PrincipalDelivery;

public sealed record WebhookEndpoint(
    string EndpointId,
    string CallbackUrl,
    bool Eligible,
    DateTimeOffset LastActiveUtc);

/// <summary>
/// Provider-SDK-free relay template. Add authentication/signing and an administrative
/// host allowlist before using tenant-configured URLs in production.
/// </summary>
public sealed class WebhookPrincipalMessageRelay : IPrincipalMessageRelay, IAsyncDisposable
{
    private const string ContextKey = "webhook:endpoints:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IPrincipalContextStore _contextStore;
    private readonly IPrincipalMessageDeliveryCompletion _completion;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Channel<PrincipalMessageDelivery> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    public WebhookPrincipalMessageRelay(
        IPrincipalContextStore contextStore,
        IPrincipalMessageDeliveryCompletion completion,
        IHttpClientFactory httpClientFactory)
    {
        _contextStore = contextStore;
        _completion = completion;
        _httpClientFactory = httpClientFactory;
        _queue = System.Threading.Channels.Channel.CreateBounded<PrincipalMessageDelivery>(
            new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => RunAsync(_stopping.Token));
    }

    public string Channel => "webhook";

    public async Task RegisterEndpointAsync(
        string principalHandle,
        string endpointId,
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        if (callback.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Webhook callbacks must use HTTPS.", nameof(callback));
        }

        var json = await _contextStore.GetAsync(
            principalHandle,
            ContextKey,
            cancellationToken);
        var endpoints = DeserializeEndpoints(json);
        endpoints.RemoveAll(endpoint => endpoint.EndpointId == endpointId);
        endpoints.Add(new WebhookEndpoint(
            endpointId,
            callback.ToString(),
            Eligible: true,
            DateTimeOffset.UtcNow));

        await _contextStore.SetAsync(
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
        if (!string.IsNullOrWhiteSpace(target?.Channel) &&
            !string.Equals(target.Channel, Channel, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());
        }

        if (message.IsSystemMessage ||
            string.IsNullOrWhiteSpace(message.Message) && message.Data is not { Length: > 0 })
        {
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());
        }

        principalContext.TryGetValue(ContextKey, out var json);
        var endpoints = DeserializeEndpoints(json);
        var endpoint = string.IsNullOrWhiteSpace(target?.EndpointId)
            ? endpoints.Where(item => item.Eligible).MaxBy(item => item.LastActiveUtc)
            : endpoints.FirstOrDefault(item =>
                item.Eligible && item.EndpointId == target.EndpointId);

        return endpoint is null
            ? ValueTask.FromResult(PrincipalMessageRelayResolution.Unavailable(Channel))
            : ValueTask.FromResult(PrincipalMessageRelayResolution.Available(
                Channel,
                endpoint.EndpointId,
                endpoint.LastActiveUtc));
    }

    public ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_queue.Writer.TryWrite(delivery));

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var delivery in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            await DeliverAsync(delivery, cancellationToken);
        }
    }

    private async Task DeliverAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        delivery.PrincipalContext.TryGetValue(ContextKey, out var json);
        var endpoint = DeserializeEndpoints(json).FirstOrDefault(item =>
            item.Eligible && item.EndpointId == delivery.EndpointId);
        if (endpoint is null)
        {
            await CompleteAsync(
                delivery,
                PrincipalMessageDeliveryOutcome.EndpointUnavailable());
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("principal-webhook");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.CallbackUrl)
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
                        ? PrincipalMessageDeliveryOutcome.RetryableFailure(
                            response.Headers.RetryAfter?.Delta)
                        : status >= 500
                            ? PrincipalMessageDeliveryOutcome.RetryableFailure()
                            : PrincipalMessageDeliveryOutcome.PermanentFailure(
                                $"Webhook returned HTTP {status}.");

            await CompleteAsync(delivery, outcome);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            await CompleteAsync(
                delivery,
                PrincipalMessageDeliveryOutcome.RetryableFailure(
                    error: exception.Message));
        }
    }

    private Task CompleteAsync(
        PrincipalMessageDelivery delivery,
        PrincipalMessageDeliveryOutcome outcome) =>
        _completion.CompleteAsync(
            delivery.PrincipalHandle,
            delivery.DeliveryId,
            outcome,
            CancellationToken.None);

    private static List<WebhookEndpoint> DeserializeEndpoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WebhookEndpoint>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public static class WebhookPrincipalMessageRelayExtensions
{
    public static IServiceCollection AddPrincipalWebhookRelay(this IServiceCollection services)
    {
        services.AddHttpClient("principal-webhook", client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        services.AddPrincipalMessageRelay<WebhookPrincipalMessageRelay>();
        return services;
    }
}
