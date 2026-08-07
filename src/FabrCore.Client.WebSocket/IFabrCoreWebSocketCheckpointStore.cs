namespace FabrCore.Client.WebSocket;

public interface IFabrCoreWebSocketCheckpointStore
{
    ValueTask<long?> GetCheckpointAsync(string clientId, CancellationToken cancellationToken = default);
    ValueTask SetCheckpointAsync(string clientId, long sequence, CancellationToken cancellationToken = default);
}

public sealed class InMemoryFabrCoreWebSocketCheckpointStore : IFabrCoreWebSocketCheckpointStore
{
    private readonly Dictionary<string, long> checkpoints = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public ValueTask<long?> GetCheckpointAsync(string clientId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return ValueTask.FromResult(checkpoints.TryGetValue(clientId, out var value) ? (long?)value : null);
    }

    public ValueTask SetCheckpointAsync(string clientId, long sequence, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!checkpoints.TryGetValue(clientId, out var current) || sequence > current)
                checkpoints[clientId] = sequence;
        }
        return ValueTask.CompletedTask;
    }
}
