using System.Text.Json;
using FabrCore.Core.Monitoring;
namespace FabrCore.Host.Services;
public partial class InMemoryAgentMessageMonitor : IAgentMonitorQueryProvider, IAgentMonitorPayloadProvider
{
    private readonly object queryLock = new();
    private readonly Queue<(long Sequence, string Kind, object Value)> queryRecords = new();
    private long querySequence;
    private string querySource = Guid.NewGuid().ToString("N");
    private void Index(string kind, object value)
    {
        lock (queryLock)
        {
            queryRecords.Enqueue((++querySequence, kind, value));
            while (queryRecords.Count > _maxMessages * 3) queryRecords.Dequeue();
        }
    }
    public Task<MonitorPage> QueryAsync(MonitorQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<(long Sequence, string Kind, object Value)> snapshot;
        string source;
        lock (queryLock) { snapshot = queryRecords.ToList(); source = querySource; }
        var records = snapshot.Select(r => Header(r.Kind, r.Value, r.Sequence)).ToList();
        if (query.IncludePayload)
        {
            var c = MonitorQueryEngine.Decode(query.Cursor, source);
            var selected = records.Where(r => r.Sequence > c.After && (c.High == 0 || r.Sequence <= c.High) && MonitorQueryEngine.Matches(r, query))
                .Take(Math.Clamp(query.Limit, 1, 1000) + 1).Select(r => r.Sequence).ToHashSet();
            var payloads = snapshot.Where(r => selected.Contains(r.Sequence)).ToDictionary(r => r.Sequence, r => r.Value);
            foreach (var r in records.Where(r => selected.Contains(r.Sequence))) r.Payload = JsonSerializer.SerializeToElement(payloads[r.Sequence], JsonSerializerOptions.Web);
        }
        return Task.FromResult(MonitorQueryEngine.Page(records, query, source, "silo", Math.Max(0, (records.FirstOrDefault()?.Sequence ?? 1) - 1)));
    }
    private static MonitorRecord Header(string kind, object value, long sequence)
    {
        var r = value switch
        {
            MonitoredMessage m => new MonitorRecord { Id = m.Id, AgentHandle = m.AgentHandle, TraceId = m.TraceId, Timestamp = m.Timestamp, Channel = m.Channel },
            MonitoredEvent e => new MonitorRecord { Id = e.Id, AgentHandle = e.AgentHandle, TraceId = e.TraceId, Timestamp = e.Timestamp, Channel = e.Channel },
            MonitoredLlmCall c => new MonitorRecord { Id = c.Id, AgentHandle = c.AgentHandle, TraceId = c.TraceId, Timestamp = c.Timestamp, Channel = c.OriginContext.StartsWith("_admin:") ? "_admin" : null },
            _ => throw new ArgumentException("Unknown monitor record.")
        };
        r.Sequence = sequence; r.Kind = kind; r.ExecutionCategory = r.Channel == "_admin" ? "admin" : "runtime"; return r;
    }
    public Task<string?> ReadPayloadAsync(long sequence, int offset, int count, CancellationToken cancellationToken = default)
    {
        object? value;
        lock (queryLock) value = queryRecords.FirstOrDefault(r => r.Sequence == sequence).Value;
        if (value is null) return Task.FromResult<string?>(null);
        var json = JsonSerializer.Serialize(value, value.GetType(), JsonSerializerOptions.Web);
        return Task.FromResult<string?>(new string(json.Skip(Math.Max(0, offset)).Take(Math.Clamp(count, 1, 32000)).ToArray()));
    }
    public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        lock (queryLock) return Task.FromResult(JsonSerializer.SerializeToElement(new { provider = "memory", sourceId = querySource,
            dataScope = "silo", retainedRecords = queryRecords.Count, durable = false, payloadsCaptured = LlmCaptureOptions.CapturePayloads }));
    }
}
