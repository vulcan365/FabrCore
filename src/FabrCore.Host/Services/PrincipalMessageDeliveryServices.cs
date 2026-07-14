using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Host.Services;

/// <summary>Orleans-backed implementation of the public principal context contract.</summary>
internal sealed class PrincipalContextStore(IGrainFactory grainFactory) : IPrincipalContextStore
{
    public Task SetAsync(
        string principalHandle,
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetPrincipal(principalHandle).SetContextValue(key, value);
    }

    public Task<string?> GetAsync(
        string principalHandle,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetPrincipal(principalHandle).GetContextValue(key);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        string principalHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GetPrincipal(principalHandle).GetContextValues();
    }

    private IPrincipalGrain GetPrincipal(string principalHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        return grainFactory.GetGrain<IPrincipalGrain>(principalHandle);
    }
}

/// <summary>Orleans-backed completion callback used by provider workers.</summary>
internal sealed class PrincipalMessageDeliveryCompletion(IGrainFactory grainFactory)
    : IPrincipalMessageDeliveryCompletion
{
    public Task CompleteAsync(
        string principalHandle,
        string deliveryId,
        PrincipalMessageDeliveryOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentNullException.ThrowIfNull(outcome);
        cancellationToken.ThrowIfCancellationRequested();

        return grainFactory
            .GetGrain<IPrincipalGrain>(principalHandle)
            .CompletePrincipalMessageDelivery(deliveryId, outcome);
    }
}

/// <summary>
/// Resolves provider-neutral delivery targets and shields the principal grain from
/// relay exceptions.
/// </summary>
internal sealed class PrincipalMessageRelayDispatcher
{
    private readonly IReadOnlyList<IPrincipalMessageRelay> _relays;
    private readonly ILogger<PrincipalMessageRelayDispatcher> _logger;

    public PrincipalMessageRelayDispatcher(
        IEnumerable<IPrincipalMessageRelay> relays,
        ILogger<PrincipalMessageRelayDispatcher> logger)
    {
        _relays = relays.ToArray();
        _logger = logger;

        _logger.LogInformation(
            "Principal message relay dispatcher initialized - RelayCount: {RelayCount}, Channels: {Channels}",
            _relays.Count,
            _relays.Count == 0
                ? "(none)"
                : string.Join(',', _relays.Select(relay => relay.Channel)));

        var duplicateChannel = _relays
            .GroupBy(relay => relay.Channel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChannel is not null)
        {
            throw new InvalidOperationException(
                $"Only one principal message relay can own channel '{duplicateChannel.Key}'.");
        }
    }

    public async Task<PrincipalMessageRelayResolution> ResolveAsync(
        string principalHandle,
        AgentMessage message,
        IReadOnlyDictionary<string, string> principalContext,
        CancellationToken cancellationToken = default)
    {
        var target = message.DeliveryTarget;
        var candidates = string.IsNullOrWhiteSpace(target?.Channel)
            ? _relays
            : _relays
                .Where(r => string.Equals(r.Channel, target.Channel, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "No principal message relay is registered for delivery target - Principal: {Principal}, MessageId: {MessageId}, TargetChannel: {TargetChannel}, RegisteredChannels: {RegisteredChannels}",
                principalHandle,
                message.Id,
                target?.Channel,
                _relays.Count == 0
                    ? "(none)"
                    : string.Join(',', _relays.Select(relay => relay.Channel)));
            return PrincipalMessageRelayResolution.Unavailable(target?.Channel);
        }

        var resolutions = new List<PrincipalMessageRelayResolution>(candidates.Count);
        foreach (var relay in candidates)
        {
            try
            {
                var resolution = await relay.ResolveAsync(
                    principalHandle,
                    message,
                    target,
                    principalContext,
                    cancellationToken);
                resolutions.Add(resolution);
                _logger.LogInformation(
                    "Principal message relay resolution completed - Principal: {Principal}, MessageId: {MessageId}, RelayChannel: {RelayChannel}, Status: {Status}, Endpoint: {Endpoint}",
                    principalHandle,
                    message.Id,
                    relay.Channel,
                    resolution.Status,
                    ShortIdentifier(resolution.EndpointId));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Principal message relay {Channel} failed while resolving delivery",
                    relay.Channel);
                resolutions.Add(PrincipalMessageRelayResolution.Unavailable(relay.Channel));
            }
        }

        var available = resolutions
            .Where(r => r.Status == PrincipalMessageRelayResolutionStatus.Available)
            .OrderByDescending(r => r.LastActiveUtc)
            .ThenBy(r => r.Channel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (available is not null)
        {
            _logger.LogInformation(
                "Principal message delivery target selected - Principal: {Principal}, MessageId: {MessageId}, Channel: {Channel}, Endpoint: {Endpoint}",
                principalHandle,
                message.Id,
                available.Channel,
                ShortIdentifier(available.EndpointId));
            return available;
        }

        var finalResolution = resolutions.Any(r => r.Status == PrincipalMessageRelayResolutionStatus.Unavailable)
            ? PrincipalMessageRelayResolution.Unavailable(target?.Channel)
            : PrincipalMessageRelayResolution.NotApplicable();
        _logger.LogWarning(
            "Principal message delivery target was not resolved - Principal: {Principal}, MessageId: {MessageId}, TargetChannel: {TargetChannel}, FinalStatus: {FinalStatus}",
            principalHandle,
            message.Id,
            target?.Channel,
            finalResolution.Status);
        return finalResolution;
    }

    public async ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        var relay = _relays.FirstOrDefault(r =>
            string.Equals(r.Channel, delivery.Channel, StringComparison.OrdinalIgnoreCase));

        if (relay is null)
        {
            _logger.LogWarning(
                "Principal delivery cannot be enqueued because its relay is not registered - DeliveryId: {DeliveryId}, Principal: {Principal}, Channel: {Channel}, RegisteredChannels: {RegisteredChannels}",
                delivery.DeliveryId,
                delivery.PrincipalHandle,
                delivery.Channel,
                _relays.Count == 0
                    ? "(none)"
                    : string.Join(',', _relays.Select(item => item.Channel)));
            return false;
        }

        try
        {
            var accepted = await relay.TryEnqueueAsync(delivery, cancellationToken);
            if (accepted)
            {
                _logger.LogInformation(
                    "Principal delivery accepted by relay - DeliveryId: {DeliveryId}, Principal: {Principal}, MessageId: {MessageId}, Channel: {Channel}, Endpoint: {Endpoint}",
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    delivery.Message.Id,
                    delivery.Channel,
                    ShortIdentifier(delivery.EndpointId));
            }
            else
            {
                _logger.LogWarning(
                    "Principal delivery rejected by relay queue - DeliveryId: {DeliveryId}, Principal: {Principal}, MessageId: {MessageId}, Channel: {Channel}, Endpoint: {Endpoint}",
                    delivery.DeliveryId,
                    delivery.PrincipalHandle,
                    delivery.Message.Id,
                    delivery.Channel,
                    ShortIdentifier(delivery.EndpointId));
            }

            return accepted;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Principal message relay {Channel} failed while accepting delivery {DeliveryId}",
                relay.Channel,
                delivery.DeliveryId);
            return false;
        }
    }

    private static string ShortIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value[..Math.Min(12, value.Length)];
}
