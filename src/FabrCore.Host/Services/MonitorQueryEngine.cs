using System.Text.Json;
using FabrCore.Core.Monitoring;
namespace FabrCore.Host.Services;

internal static class MonitorQueryEngine
{
    internal sealed record Cursor(string Source, long After, long High, string? Filter = null);
    internal static string Filter(MonitorQuery query) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { query.Principal, query.AgentHandle, query.TraceId, query.Kind, query.Channel, query.ExecutionCategory, query.From, query.To })));
    internal static Cursor Decode(string? cursor, string source)
    {
        if (cursor is null) return new(source, 0, 0);
        try
        {
            var parsed = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor));
            if (parsed is null || string.IsNullOrEmpty(parsed.Source) || parsed.After < 0 || parsed.High < 0 ||
                (parsed.High != 0 && parsed.High < parsed.After)) throw new ArgumentException("Invalid monitor cursor.");
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ArgumentException("Invalid monitor cursor."); }
    }
    internal static string Encode(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));
    internal static bool Matches(MonitorRecord r, MonitorQuery q) =>
        (q.Kind is null || r.Kind == q.Kind) && (q.AgentHandle is null || r.AgentHandle == q.AgentHandle) &&
        (q.Principal is null || r.AgentHandle?.StartsWith(q.Principal + ":", StringComparison.OrdinalIgnoreCase) == true) &&
        (q.TraceId is null || r.TraceId == q.TraceId) && (q.Channel is null || r.Channel == q.Channel) &&
        (q.ExecutionCategory is null || r.ExecutionCategory == q.ExecutionCategory) &&
        (q.From is null || r.Timestamp >= q.From) && (q.To is null || r.Timestamp <= q.To);
    internal static MonitorRecord Record(string kind, object value, long sequence = 0)
    {
        var json = JsonSerializer.SerializeToElement(value, value.GetType(), JsonSerializerOptions.Web);
        string? Read(string key) => json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var origin = Read("originContext");
        return new() { Sequence = sequence, Kind = kind, Id = Read("id")!, AgentHandle = Read("agentHandle"), TraceId = Read("traceId"),
            Channel = Read("channel") ?? (origin?.StartsWith("_admin:") == true ? "_admin" : null),
            ExecutionCategory = origin?.StartsWith("_admin:") == true || Read("channel") == "_admin" ? "admin" : "runtime",
            Timestamp = json.GetProperty("timestamp").GetDateTimeOffset(), Payload = json };
    }
    internal static MonitorPage Page(List<MonitorRecord> records, MonitorQuery query, string source, string scope, long floor = 0)
    {
        var c = Decode(query.Cursor, source);
        if (c.Filter is not null && c.Filter != Filter(query)) throw new ArgumentException("Monitor filters changed; restart pagination.");
        if (c.Source != source || (c.After != 0 && c.After < floor)) return new() { SourceId = source, DataScope = scope, Gap = true, Notice = "Source restarted or records expired; restart the query." };
        var high = c.High == 0 ? records.LastOrDefault()?.Sequence ?? c.After : c.High;
        var filtered = records.Where(r => r.Sequence > c.After && r.Sequence <= high && Matches(r, query)).Take(Math.Clamp(query.Limit, 1, 1000) + 1).ToList();
        var page = new MonitorPage { SourceId = source, DataScope = scope };
        var bytes = 0;
        foreach (var record in filtered.Take(Math.Clamp(query.Limit, 1, 1000)))
        {
            if (!query.IncludePayload) record.Payload = null;
            var size = JsonSerializer.SerializeToUtf8Bytes(record).Length;
            var maximum = Math.Clamp(query.MaxBytes, 512, 512 * 1024);
            if (size > maximum) throw new ArgumentException("Record payload is too large; retrieve it through the payload endpoint.");
            if (bytes + size > maximum) break;
            bytes += size; page.Items.Add(record);
        }
        page.HasMore = filtered.Count > page.Items.Count;
        page.NextCursor = Encode(new(source, page.HasMore ? page.Items[^1].Sequence : high, page.HasMore ? high : 0, Filter(query)));
        return page;
    }
}
