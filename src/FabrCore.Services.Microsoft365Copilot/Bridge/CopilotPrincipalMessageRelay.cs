using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

internal sealed class CopilotPrincipalMessageRelay : IPrincipalMessageRelay
{
    private readonly CopilotProactiveMessenger _messenger;
    private readonly ILogger<CopilotPrincipalMessageRelay> _logger;

    public CopilotPrincipalMessageRelay(
        CopilotProactiveMessenger messenger,
        ILogger<CopilotPrincipalMessageRelay> logger)
    {
        _messenger = messenger;
        _logger = logger;
    }

    public string Channel => Microsoft365CopilotDefaults.ChannelName;

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
            _logger.LogDebug(
                "Microsoft 365 relay is not applicable to targeted message - Principal: {Principal}, MessageId: {MessageId}, TargetChannel: {TargetChannel}",
                principalHandle,
                message.Id,
                target.Channel);
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());
        }

        if (!IsSupportedMessage(message))
        {
            _logger.LogInformation(
                "Microsoft 365 relay rejected unsupported message - Principal: {Principal}, MessageId: {MessageId}, MessageType: {MessageType}, IsSystemMessage: {IsSystemMessage}, HasText: {HasText}",
                principalHandle,
                message.Id,
                message.MessageType,
                message.IsSystemMessage,
                !string.IsNullOrWhiteSpace(message.Message));
            return ValueTask.FromResult(PrincipalMessageRelayResolution.NotApplicable());
        }

        principalContext.TryGetValue(
            Microsoft365CopilotDefaults.ProactiveEndpointsContextKey,
            out var json);
        if (!CopilotConversationContextWriter.TryParseRegistry(json, out var registry))
        {
            _logger.LogWarning(
                "Microsoft 365 relay could not resolve message because the endpoint registry is malformed - Principal: {Principal}, MessageId: {MessageId}, RegistryPresent: {RegistryPresent}",
                principalHandle,
                message.Id,
                !string.IsNullOrWhiteSpace(json));
            return ValueTask.FromResult(
                PrincipalMessageRelayResolution.Unavailable(Channel));
        }

        var endpoint = string.IsNullOrWhiteSpace(target?.EndpointId)
            ? registry.Endpoints
                .Where(item => item.Eligible)
                .OrderByDescending(item => item.LastActiveUtc)
                .FirstOrDefault()
            : registry.Endpoints.FirstOrDefault(item =>
                item.Eligible &&
                string.Equals(item.EndpointId, target.EndpointId, StringComparison.Ordinal));

        if (endpoint is null)
        {
            _logger.LogWarning(
                "Microsoft 365 relay found no eligible endpoint - Principal: {Principal}, MessageId: {MessageId}, RequestedEndpoint: {RequestedEndpoint}, RegistryPresent: {RegistryPresent}, StoredEndpoints: {StoredEndpoints}, EligibleEndpoints: {EligibleEndpoints}",
                principalHandle,
                message.Id,
                CopilotConversationContextWriter.ShortEndpointId(target?.EndpointId),
                !string.IsNullOrWhiteSpace(json),
                registry.Endpoints.Count,
                registry.Endpoints.Count(item => item.Eligible));
            return ValueTask.FromResult(PrincipalMessageRelayResolution.Unavailable(Channel));
        }

        _logger.LogInformation(
            "Microsoft 365 relay resolved endpoint - Principal: {Principal}, MessageId: {MessageId}, RequestedEndpoint: {RequestedEndpoint}, ResolvedEndpoint: {ResolvedEndpoint}, ConversationType: {ConversationType}, StoredEndpoints: {StoredEndpoints}",
            principalHandle,
            message.Id,
            CopilotConversationContextWriter.ShortEndpointId(target?.EndpointId),
            CopilotConversationContextWriter.ShortEndpointId(endpoint.EndpointId),
            endpoint.ConversationType,
            registry.Endpoints.Count);
        return ValueTask.FromResult(PrincipalMessageRelayResolution.Available(
            Channel,
            endpoint.EndpointId,
            endpoint.LastActiveUtc));
    }

    public ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_messenger.TryEnqueue(delivery));

    internal static bool IsSupportedMessage(AgentMessage message)
    {
        if (message.IsSystemMessage)
        {
            return false;
        }

        if (message.MessageType?.StartsWith("ui.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return CopilotActivityMapper.IsAdaptiveCardRender(message);
        }

        return !string.IsNullOrWhiteSpace(message.Message);
    }
}

internal interface ICopilotProactiveActivitySender
{
    Task SendAsync(
        Conversation conversation,
        IActivity activity,
        CancellationToken cancellationToken);
}

internal sealed class CopilotProactiveActivitySender(IServiceProvider serviceProvider)
    : ICopilotProactiveActivitySender
{
    public Task SendAsync(
        Conversation conversation,
        IActivity activity,
        CancellationToken cancellationToken)
    {
        var adapter = serviceProvider.GetService<IChannelAdapter>()
            ?? serviceProvider.GetService<IAgentHttpAdapter>() as IChannelAdapter
            ?? throw new InvalidOperationException(
                "The Microsoft Agents channel adapter is not registered.");

        return Proactive.SendActivityAsync(adapter, conversation, activity, cancellationToken);
    }
}

internal sealed class CopilotProactiveMessenger : IAsyncDisposable
{
    private readonly Channel<PrincipalMessageDelivery>[] _queues;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ICopilotProactiveActivitySender _sender;
    private readonly IPrincipalMessageDeliveryCompletion _completion;
    private readonly ICopilotConversationContextWriter _contextWriter;
    private readonly CopilotProactiveOptions _options;
    private readonly ILogger<CopilotProactiveMessenger> _logger;

    public CopilotProactiveMessenger(
        ICopilotProactiveActivitySender sender,
        IPrincipalMessageDeliveryCompletion completion,
        ICopilotConversationContextWriter contextWriter,
        IOptions<Microsoft365CopilotOptions> options,
        ILogger<CopilotProactiveMessenger> logger)
    {
        _sender = sender;
        _completion = completion;
        _contextWriter = contextWriter;
        _options = options.Value.Proactive;
        _logger = logger;

        _queues = Enumerable.Range(0, _options.WorkerShards)
            .Select(_ => Channel.CreateBounded<PrincipalMessageDelivery>(new BoundedChannelOptions(
                _options.OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            }))
            .ToArray();
        _workers = _queues
            .Select(queue => Task.Run(() => RunWorkerAsync(queue.Reader, _stopping.Token)))
            .ToArray();

        _logger.LogInformation(
            "Microsoft 365 proactive delivery workers started - WorkerShards: {WorkerShards}, QueueCapacityPerShard: {QueueCapacity}, MaxDeliveryAttempts: {MaxDeliveryAttempts}, SendTimeout: {SendTimeout}",
            _options.WorkerShards,
            _options.OutboundQueueCapacity,
            _options.MaxDeliveryAttempts,
            _options.SendTimeout);
    }

    public bool TryEnqueue(PrincipalMessageDelivery delivery)
    {
        var index = (delivery.PrincipalHandle.GetHashCode(StringComparison.Ordinal) & int.MaxValue)
            % _queues.Length;
        var accepted = _queues[index].Writer.TryWrite(delivery);
        if (accepted)
        {
            _logger.LogInformation(
                "Microsoft 365 proactive delivery accepted by worker queue - DeliveryId: {DeliveryId}, Principal: {Principal}, MessageId: {MessageId}, Endpoint: {Endpoint}, Shard: {Shard}, AttemptNumber: {AttemptNumber}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                delivery.Message.Id,
                CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                index,
                delivery.AttemptNumber);
        }
        else
        {
            _logger.LogWarning(
                "Microsoft 365 proactive worker queue rejected delivery - DeliveryId: {DeliveryId}, Principal: {Principal}, MessageId: {MessageId}, Endpoint: {Endpoint}, Shard: {Shard}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                delivery.Message.Id,
                CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                index);
        }

        return accepted;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var queue in _queues)
        {
            queue.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private async Task RunWorkerAsync(
        ChannelReader<PrincipalMessageDelivery> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var delivery in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessAsync(delivery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // An unanticipated worker failure is retryable. If reporting also
                // fails, Core recovers the lease and tries again.
                _logger.LogError(
                    exception,
                    "Unexpected Microsoft 365 proactive worker failure for delivery {DeliveryId}",
                    delivery.DeliveryId);
                await TryCompleteAsync(
                    delivery,
                    PrincipalMessageDeliveryOutcome.RetryableFailure(
                        _options.RetryBaseDelay,
                        exception.Message));
            }
        }
    }

    private async Task ProcessAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Microsoft 365 proactive worker processing delivery - DeliveryId: {DeliveryId}, Principal: {Principal}, MessageId: {MessageId}, Endpoint: {Endpoint}, AttemptNumber: {AttemptNumber}",
            delivery.DeliveryId,
            delivery.PrincipalHandle,
            delivery.Message.Id,
            CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
            delivery.AttemptNumber);

        if (!TryBuildConversation(delivery, out var conversation, out var endpointError))
        {
            _logger.LogWarning(
                "Microsoft 365 proactive delivery could not build conversation - DeliveryId: {DeliveryId}, Principal: {Principal}, Endpoint: {Endpoint}, Reason: {Reason}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                endpointError);
            await TryCompleteAsync(
                delivery,
                PrincipalMessageDeliveryOutcome.EndpointUnavailable(
                    error: endpointError));
            return;
        }

        if (!TryBuildActivity(delivery.Message, out var activity, out var mappingError))
        {
            _logger.LogWarning(
                "Microsoft 365 proactive delivery could not map activity - DeliveryId: {DeliveryId}, MessageId: {MessageId}, MessageType: {MessageType}, Reason: {Reason}",
                delivery.DeliveryId,
                delivery.Message.Id,
                delivery.Message.MessageType,
                mappingError);
            await TryCompleteAsync(
                delivery,
                PrincipalMessageDeliveryOutcome.PermanentFailure(mappingError));
            return;
        }

        Exception? lastException = null;
        TimeSpan? lastRetryAfter = null;
        for (var attempt = 1; attempt <= _options.MaxDeliveryAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SendTimeout);
            try
            {
                _logger.LogInformation(
                    "Sending Microsoft 365 proactive activity - DeliveryId: {DeliveryId}, Principal: {Principal}, Endpoint: {Endpoint}, ProviderAttempt: {ProviderAttempt}, MaxProviderAttempts: {MaxProviderAttempts}",
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                    attempt,
                    _options.MaxDeliveryAttempts);
                await _sender.SendAsync(conversation!, activity!, timeout.Token);
                _logger.LogInformation(
                    "Microsoft 365 proactive activity sent successfully - DeliveryId: {DeliveryId}, Principal: {Principal}, Endpoint: {Endpoint}, ProviderAttempt: {ProviderAttempt}",
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                    attempt);
                await TryCompleteAsync(delivery, PrincipalMessageDeliveryOutcome.Delivered());
                return;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                var classification = Classify(exception);
                lastRetryAfter = ReadRetryAfter(exception);
                var statusCode = GetStatusCode(exception);

                _logger.LogWarning(
                    "Microsoft 365 proactive activity send failed - DeliveryId: {DeliveryId}, Principal: {Principal}, Endpoint: {Endpoint}, ProviderAttempt: {ProviderAttempt}, Classification: {Classification}, StatusCode: {StatusCode}, ExceptionType: {ExceptionType}, RetryAfter: {RetryAfter}",
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    CopilotConversationContextWriter.ShortEndpointId(delivery.EndpointId),
                    attempt,
                    classification,
                    statusCode is null ? "(none)" : $"{(int)statusCode} {statusCode}",
                    exception.GetType().FullName,
                    lastRetryAfter);

                if (classification == SendFailureClassification.EndpointUnavailable)
                {
                    try
                    {
                        await _contextWriter.MarkUnavailableAsync(
                            delivery.PrincipalHandle,
                            delivery.EndpointId,
                            cancellationToken);
                    }
                    catch (Exception contextException)
                    {
                        _logger.LogWarning(
                            contextException,
                            "Could not mark Microsoft 365 endpoint {EndpointId} unavailable",
                            delivery.EndpointId);
                    }

                    await TryCompleteAsync(
                        delivery,
                        PrincipalMessageDeliveryOutcome.EndpointUnavailable(
                            lastRetryAfter,
                            exception.Message));
                    return;
                }

                if (classification == SendFailureClassification.Permanent)
                {
                    await TryCompleteAsync(
                        delivery,
                        PrincipalMessageDeliveryOutcome.PermanentFailure(exception.Message));
                    return;
                }

                if (attempt < _options.MaxDeliveryAttempts)
                {
                    var delay = lastRetryAfter ?? TimeSpan.FromTicks(
                        _options.RetryBaseDelay.Ticks * (1L << (attempt - 1)));
                    _logger.LogInformation(
                        "Microsoft 365 proactive delivery scheduled for provider retry - DeliveryId: {DeliveryId}, NextProviderAttempt: {NextProviderAttempt}, Delay: {Delay}",
                        delivery.DeliveryId,
                        attempt + 1,
                        delay);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        await TryCompleteAsync(
            delivery,
            PrincipalMessageDeliveryOutcome.RetryableFailure(
                lastRetryAfter ?? _options.RetryBaseDelay,
                lastException?.Message));
    }

    private async Task TryCompleteAsync(
        PrincipalMessageDelivery delivery,
        PrincipalMessageDeliveryOutcome outcome)
    {
        try
        {
            _logger.LogInformation(
                "Reporting Microsoft 365 proactive delivery outcome - DeliveryId: {DeliveryId}, Principal: {Principal}, Outcome: {Outcome}, RetryAfter: {RetryAfter}, HasError: {HasError}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                outcome.Kind,
                outcome.RetryAfter,
                !string.IsNullOrWhiteSpace(outcome.Error));
            await _completion.CompleteAsync(
                delivery.PrincipalHandle,
                delivery.DeliveryId,
                outcome,
                CancellationToken.None);
            _logger.LogInformation(
                "Microsoft 365 proactive delivery outcome recorded - DeliveryId: {DeliveryId}, Principal: {Principal}, Outcome: {Outcome}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                outcome.Kind);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not report Microsoft 365 delivery completion for {DeliveryId}; Core will recover its lease",
                delivery.DeliveryId);
        }
    }

    internal static bool TryBuildConversation(
        PrincipalMessageDelivery delivery,
        out Conversation? conversation,
        out string? error)
    {
        conversation = null;
        error = null;
        delivery.PrincipalContext.TryGetValue(
            Microsoft365CopilotDefaults.ProactiveEndpointsContextKey,
            out var json);
        if (!CopilotConversationContextWriter.TryParseRegistry(json, out var registry))
        {
            error = "The Microsoft 365 endpoint registry is malformed.";
            return false;
        }

        var endpoint = registry.Endpoints.FirstOrDefault(item =>
            item.Eligible &&
            string.Equals(item.EndpointId, delivery.EndpointId, StringComparison.Ordinal));
        if (endpoint is null)
        {
            error = "The selected Microsoft 365 conversation endpoint is unavailable.";
            return false;
        }

        try
        {
            var reference = JsonSerializer.Deserialize<ConversationReference>(
                endpoint.ConversationReferenceJson,
                ProtocolJsonSerializer.SerializationOptions);
            if (reference is null)
            {
                error = "The Microsoft 365 conversation reference is empty.";
                return false;
            }

            conversation = new Conversation(endpoint.Claims, reference);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            error = $"The Microsoft 365 conversation reference is invalid: {exception.Message}";
            return false;
        }
    }

    internal static bool TryBuildActivity(
        AgentMessage message,
        out IActivity? activity,
        out string? error)
    {
        activity = null;
        error = null;
        if (CopilotActivityMapper.IsAdaptiveCardRender(message))
        {
            if (!CopilotActivityMapper.TryCreateAdaptiveCardAttachment(message, out var attachment))
            {
                error = "The adaptive-card payload could not be mapped.";
                return false;
            }

            activity = MessageFactory.Attachment(attachment!);
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                activity.Text = message.Message;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            error = "The proactive text message is blank.";
            return false;
        }

        activity = MessageFactory.Text(message.Message);
        return true;
    }

    private static SendFailureClassification Classify(Exception exception)
    {
        var statusCode = GetStatusCode(exception);
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return SendFailureClassification.EndpointUnavailable;
        }

        if (statusCode == HttpStatusCode.TooManyRequests || (int?)statusCode >= 500)
        {
            return SendFailureClassification.Retryable;
        }

        if ((int?)statusCode is >= 400 and < 500)
        {
            return SendFailureClassification.Permanent;
        }

        return exception is HttpRequestException or TimeoutException or TaskCanceledException or OperationCanceledException
            ? SendFailureClassification.Retryable
            : SendFailureClassification.Permanent;
    }

    private static HttpStatusCode? GetStatusCode(Exception exception)
    {
        if (exception is HttpRequestException requestException)
        {
            return requestException.StatusCode;
        }

        var value = exception.GetType().GetProperty("StatusCode")?.GetValue(exception);
        return value switch
        {
            HttpStatusCode statusCode => statusCode,
            int numeric when Enum.IsDefined(typeof(HttpStatusCode), numeric) => (HttpStatusCode)numeric,
            _ => exception.InnerException is null ? null : GetStatusCode(exception.InnerException)
        };
    }

    private static TimeSpan? ReadRetryAfter(Exception exception)
    {
        var response = exception.GetType().GetProperty("Response")?.GetValue(exception);
        if (response is HttpResponseMessage httpResponse &&
            httpResponse.Headers.RetryAfter is { } retryAfterHeader)
        {
            if (retryAfterHeader.Delta is { } delta)
            {
                return delta;
            }

            if (retryAfterHeader.Date is { } date)
            {
                var delay = date - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        foreach (var key in new[] { "Retry-After", "RetryAfter" })
        {
            if (!exception.Data.Contains(key))
            {
                continue;
            }

            var value = exception.Data[key];
            if (value is TimeSpan delay)
            {
                return delay;
            }

            if (value is int seconds)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (TimeSpan.TryParse(value?.ToString(), out delay))
            {
                return delay;
            }

            if (int.TryParse(value?.ToString(), out seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        var retryAfter = exception.GetType().GetProperty("RetryAfter")?.GetValue(exception);
        if (retryAfter is TimeSpan reflectedDelay)
        {
            return reflectedDelay;
        }

        return exception.InnerException is null ? null : ReadRetryAfter(exception.InnerException);
    }

    private enum SendFailureClassification
    {
        Retryable,
        EndpointUnavailable,
        Permanent
    }
}
