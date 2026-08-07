using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace FabrCore.Host.Grains;

internal sealed class WebSocketDeliveryGrain : Grain, IWebSocketDeliveryGrain
{
    private readonly IPersistentState<FabrCoreWebSocketDeliveryState> state;
    private readonly FabrCoreWebSocketOptions options;
    private readonly TimeProvider timeProvider;

    public WebSocketDeliveryGrain(
        [PersistentState("webSocketDelivery", FabrCoreOrleansConstants.StorageProviderName)]
        IPersistentState<FabrCoreWebSocketDeliveryState> state,
        IOptions<FabrCoreWebSocketOptions> options,
        TimeProvider timeProvider)
    {
        this.state = state;
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    public async Task<FabrCoreWebSocketRegistration> RegisterClient(string clientId, long? checkpoint)
    {
        ValidateClientId(clientId);
        var now = timeProvider.GetUtcNow();
        Prune(now);

        var isNew = !state.State.Clients.TryGetValue(clientId, out var cursor);
        if (isNew)
        {
            if (state.State.Clients.Count >= options.MaxClientsPerPrincipal)
                throw new InvalidOperationException($"A principal may register at most {options.MaxClientsPerPrincipal} WebSocket clients.");

            cursor = new FabrCoreWebSocketClientCursor
            {
                AcknowledgedSequence = state.State.CurrentSequence,
                HighestDeliveredSequence = state.State.CurrentSequence,
                LastSeenAt = now,
            };
            state.State.Clients[clientId] = cursor;
        }

        cursor!.LastSeenAt = now;
        var effective = isNew
            ? state.State.CurrentSequence
            : Math.Min(cursor.AcknowledgedSequence, checkpoint ?? cursor.AcknowledgedSequence);
        var oldest = state.State.Deliveries.Count == 0 ? (long?)null : state.State.Deliveries[0].Sequence;
        long? gapAfter = oldest.HasValue && effective < oldest.Value - 1 ? effective : null;
        var replayAfter = gapAfter.HasValue ? oldest!.Value - 1 : effective;
        var replay = state.State.Deliveries.Where(x => x.Sequence > replayAfter).ToList();
        if (replay.Count > 0)
            cursor.HighestDeliveredSequence = Math.Max(cursor.HighestDeliveredSequence, replay[^1].Sequence);

        await state.WriteStateAsync();
        return new FabrCoreWebSocketRegistration
        {
            CurrentSequence = state.State.CurrentSequence,
            OldestAvailableSequence = oldest,
            GapAfterSequence = gapAfter,
            Replay = replay,
        };
    }

    public async Task<FabrCoreWebSocketDeliveryRecord?> Append(AgentMessage message)
    {
        var now = timeProvider.GetUtcNow();
        Prune(now);
        if (state.State.Clients.Count == 0)
        {
            if (state.RecordExists)
                await state.WriteStateAsync();
            return null;
        }

        var existing = state.State.Deliveries.FirstOrDefault(x => x.Message.Id == message.Id);
        if (existing is not null)
            return existing;

        var record = new FabrCoreWebSocketDeliveryRecord
        {
            Sequence = ++state.State.CurrentSequence,
            DeliveryId = Guid.NewGuid().ToString("N"),
            Message = message,
            CreatedAt = now,
        };
        state.State.Deliveries.Add(record);
        EnforceBounds(now);
        await state.WriteStateAsync();
        return record;
    }

    public async Task MarkDelivered(string clientId, long sequence)
    {
        if (state.State.Clients.TryGetValue(clientId, out var cursor))
        {
            cursor.HighestDeliveredSequence = Math.Max(cursor.HighestDeliveredSequence, sequence);
            cursor.LastSeenAt = timeProvider.GetUtcNow();
            await state.WriteStateAsync();
        }
    }

    public async Task Acknowledge(string clientId, long sequence)
    {
        if (!state.State.Clients.TryGetValue(clientId, out var cursor))
            throw new InvalidOperationException("The WebSocket client is not registered.");
        if (sequence < cursor.AcknowledgedSequence)
            return;
        if (sequence > cursor.HighestDeliveredSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence), "An acknowledgement cannot exceed the highest delivered sequence.");

        cursor.AcknowledgedSequence = sequence;
        cursor.LastSeenAt = timeProvider.GetUtcNow();
        Prune(timeProvider.GetUtcNow());
        await state.WriteStateAsync();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var clientId in state.State.Clients
            .Where(x => now - x.Value.LastSeenAt > options.InactiveClientExpiration)
            .Select(x => x.Key).ToArray())
            state.State.Clients.Remove(clientId);

        EnforceBounds(now);
        if (state.State.Clients.Count > 0)
        {
            var minimumAck = state.State.Clients.Values.Min(x => x.AcknowledgedSequence);
            state.State.Deliveries.RemoveAll(x => x.Sequence <= minimumAck);
        }
    }

    private void EnforceBounds(DateTimeOffset now)
    {
        state.State.Deliveries.RemoveAll(x => now - x.CreatedAt > options.DeliveryRetention);
        var excess = state.State.Deliveries.Count - options.MaxDeliveriesPerPrincipal;
        if (excess > 0)
            state.State.Deliveries.RemoveRange(0, excess);
    }

    private static void ValidateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128)
            throw new ArgumentException("clientId is required and must not exceed 128 characters.", nameof(clientId));
    }
}
