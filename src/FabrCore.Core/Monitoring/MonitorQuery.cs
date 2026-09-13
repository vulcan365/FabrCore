using System.Text.Json;
namespace FabrCore.Core.Monitoring;

public sealed class MonitorQuery
{
    public string? Principal { get; set; }
    public string? AgentHandle { get; set; }
    public string? TraceId { get; set; }
    public string? Kind { get; set; }
    public string? Channel { get; set; }
    public string? ExecutionCategory { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string? Cursor { get; set; }
    public int Limit { get; set; } = 100;
    public bool IncludePayload { get; set; }
    public int MaxBytes { get; set; } = 512 * 1024;
}

public sealed class MonitorRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? AgentHandle { get; set; }
    public string? TraceId { get; set; }
    public string? Channel { get; set; }
    public string ExecutionCategory { get; set; } = "runtime";
    public DateTimeOffset Timestamp { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class MonitorPage
{
    public List<MonitorRecord> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public bool Gap { get; set; }
    public string? Notice { get; set; }
    public string SourceId { get; set; } = "";
    public string DataScope { get; set; } = "silo";
}

public interface IAgentMonitorQueryProvider
{
    Task<MonitorPage> QueryAsync(MonitorQuery query, CancellationToken cancellationToken = default);
    Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken = default);
}
public interface IAgentMonitorPayloadProvider
{
    Task<string?> ReadPayloadAsync(long sequence, int offset, int count, CancellationToken cancellationToken = default);
}
