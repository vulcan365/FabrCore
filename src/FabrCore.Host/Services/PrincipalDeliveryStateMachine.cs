using FabrCore.Core;
using FabrCore.Host.Configuration;

namespace FabrCore.Host.Services;

internal static class PrincipalDeliveryStateMachine
{
    public static bool ShouldDeliverToObservers(int observerCount) => observerCount > 0;

    public static bool TryRecoverExpiredLease(
        PrincipalDeliveryOutboxEntry entry,
        DateTimeOffset now)
    {
        if (entry.LeaseExpiresUtc is null || entry.LeaseExpiresUtc > now)
        {
            return false;
        }

        entry.LeaseExpiresUtc = null;
        entry.LastError = "Delivery lease expired before completion was reported.";
        return true;
    }

    public static bool TryAcquireLease(
        PrincipalDeliveryOutboxEntry entry,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        if (entry.AvailableAfterUtc > now ||
            entry.LeaseExpiresUtc is { } leaseExpiresUtc && leaseExpiresUtc > now)
        {
            return false;
        }

        entry.LeaseExpiresUtc = now + leaseDuration;
        return true;
    }

    public static void ApplyRetryableFailure(
        PrincipalDeliveryOutboxEntry entry,
        PrincipalMessageDeliveryOutcome outcome,
        PrincipalDeliveryOptions options,
        DateTimeOffset now)
    {
        entry.AttemptCount++;
        entry.LeaseExpiresUtc = null;
        entry.WaitingForEndpointRefresh = false;
        entry.AvailableAfterUtc = now + NormalizeRetryDelay(outcome.RetryAfter, options);
        entry.LastError = NormalizeError(outcome.Error);
    }

    public static void ApplyEndpointUnavailable(
        PrincipalDeliveryOutboxEntry entry,
        PrincipalMessageDeliveryOutcome outcome,
        PrincipalDeliveryOptions options,
        DateTimeOffset now)
    {
        entry.AttemptCount++;
        entry.LeaseExpiresUtc = null;
        entry.WaitingForEndpointRefresh = true;
        entry.AvailableAfterUtc = outcome.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? now + NormalizeRetryDelay(retryAfter, options)
            : entry.CreatedUtc + options.MaxDeliveryAge;
        entry.LastError = NormalizeError(outcome.Error);
    }

    public static PrincipalDeliveryDeadLetter MoveToDeadLetters(
        PrincipalGrainState state,
        PrincipalDeliveryOutboxEntry entry,
        string reason,
        PrincipalDeliveryOptions options,
        DateTimeOffset now)
    {
        state.DeliveryOutbox.Remove(entry);
        var deadLetter = new PrincipalDeliveryDeadLetter
        {
            DeliveryId = entry.DeliveryId,
            Message = entry.Message,
            Channel = entry.Channel,
            EndpointId = entry.EndpointId,
            CreatedUtc = entry.CreatedUtc,
            DeadLetteredUtc = now,
            AttemptCount = entry.AttemptCount,
            Reason = NormalizeError(reason)
        };
        state.DeliveryDeadLetters.Add(deadLetter);
        PruneDeadLetters(state, options, now);
        return deadLetter;
    }

    public static void PruneDeadLetters(
        PrincipalGrainState state,
        PrincipalDeliveryOptions options,
        DateTimeOffset now)
    {
        state.DeliveryDeadLetters.RemoveAll(item =>
            now - item.DeadLetteredUtc > options.DeadLetterRetention);

        var maxDeadLetters = Math.Max(0, options.MaxDeadLetters);
        var overflow = state.DeliveryDeadLetters.Count - maxDeadLetters;
        if (overflow > 0)
        {
            state.DeliveryDeadLetters.RemoveRange(0, overflow);
        }
    }

    private static TimeSpan NormalizeRetryDelay(
        TimeSpan? retryAfter,
        PrincipalDeliveryOptions options)
    {
        if (retryAfter is null || retryAfter <= TimeSpan.Zero)
        {
            return options.RecoveryReminderPeriod < TimeSpan.FromMinutes(1)
                ? TimeSpan.FromMinutes(1)
                : options.RecoveryReminderPeriod;
        }

        return retryAfter.Value > options.MaxDeliveryAge
            ? options.MaxDeliveryAge
            : retryAfter.Value;
    }

    private static string? NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        const int maxLength = 2048;
        return error.Length <= maxLength ? error : error[..maxLength];
    }
}
