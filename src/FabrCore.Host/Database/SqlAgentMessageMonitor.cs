using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Database;

/// <summary>Opt-in retained monitoring. Agent calls enqueue bounded records and never await SQL.</summary>
public sealed class SqlAgentMessageMonitor : BackgroundService, IAgentMessageMonitor, IAgentMonitorQueryProvider, IAgentMonitorPayloadProvider
{
    private readonly OperationalDatabase database;
    private readonly ILogger<SqlAgentMessageMonitor> logger;
    private readonly InMemoryAgentMessageMonitor recent;
    private readonly Channel<(MonitorRecord Record, int Bytes)> queue;
    private readonly long maxBytes;
    private readonly int batchSize;
    private readonly int retentionDays;
    private long queuedBytes, queuedRecords, dropped, failedWrites;
    private DateTimeOffset? lastWrite;
    private string? source;
    public SqlAgentMessageMonitor(OperationalDatabase database, IConfiguration configuration, LlmCaptureOptions capture,
        ILogger<SqlAgentMessageMonitor> logger, ILogger<InMemoryAgentMessageMonitor> recentLogger)
    {
        this.database = database; this.logger = logger;
        recent = new(recentLogger, capture);
        maxBytes = Math.Clamp(configuration.GetValue<long?>("FabrCore:Monitoring:QueueBytes") ?? 64 * 1024 * 1024, 1024, 1024L * 1024 * 1024);
        batchSize = Math.Clamp(configuration.GetValue<int?>("FabrCore:Monitoring:BatchSize") ?? 250, 1, 250);
        retentionDays = Math.Clamp(configuration.GetValue<int?>("FabrCore:Monitoring:RetentionDays") ?? 7, 1, 3650);
        queue = Channel.CreateBounded<(MonitorRecord, int)>(new BoundedChannelOptions(Math.Clamp(configuration.GetValue<int?>("FabrCore:Monitoring:QueueRecords") ?? 10000, 1, 100000))
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }
    public LlmCaptureOptions LlmCaptureOptions => recent.LlmCaptureOptions;
    public event Action<MonitoredMessage>? OnMessageRecorded { add => recent.OnMessageRecorded += value; remove => recent.OnMessageRecorded -= value; }
    public event Action<MonitoredEvent>? OnEventRecorded { add => recent.OnEventRecorded += value; remove => recent.OnEventRecorded -= value; }
    public event Action<MonitoredLlmCall>? OnLlmCallRecorded { add => recent.OnLlmCallRecorded += value; remove => recent.OnLlmCallRecorded -= value; }
    public Task RecordMessageAsync(MonitoredMessage message) { Enqueue("message", message); return recent.RecordMessageAsync(message); }
    public Task RecordEventAsync(MonitoredEvent evt) { Enqueue("event", evt); return recent.RecordEventAsync(evt); }
    public Task RecordLlmCallAsync(MonitoredLlmCall call) { if (LlmCaptureOptions.Enabled) Enqueue("llm", call); return recent.RecordLlmCallAsync(call); }
    public Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null) => recent.GetMessagesAsync(agentHandle, limit);
    public Task<List<MonitoredEvent>> GetEventsAsync(string? agentHandle = null, int? limit = null) => recent.GetEventsAsync(agentHandle, limit);
    public Task<List<MonitoredLlmCall>> GetLlmCallsAsync(string? agentHandle = null, int? limit = null) => recent.GetLlmCallsAsync(agentHandle, limit);
    public Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle) => recent.GetAgentTokenSummaryAsync(agentHandle);
    public Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync() => recent.GetAllAgentTokenSummariesAsync();
    public Task ClearAsync() => recent.ClearAsync(); // Durable records expire through the retention policy.
    private void Enqueue(string kind, object data)
    {
        try
        {
            var record = MonitorQueryEngine.Record(kind, data);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record).Length;
            if (Interlocked.Add(ref queuedBytes, bytes) > maxBytes)
            { Interlocked.Add(ref queuedBytes, -bytes); Interlocked.Increment(ref dropped); return; }
            Interlocked.Increment(ref queuedRecords);
            if (!queue.Writer.TryWrite((record, bytes)))
            { Interlocked.Add(ref queuedBytes, -bytes); Interlocked.Decrement(ref queuedRecords); Interlocked.Increment(ref dropped); }
        }
        catch (Exception ex) { Interlocked.Increment(ref dropped); logger.LogWarning(ex, "Monitor capture could not be enqueued"); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<(MonitorRecord Record, int Bytes)>();
        var failures = 0;
        var cleanupAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    while (batch.Count < batchSize && queue.Reader.TryRead(out var item)) batch.Add(item);
                }
                if (batch.Count > 0)
                {
                    await using var connection = await database.OpenAsync(stoppingToken);
                    await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(stoppingToken);
                    await OperationalDatabase.LockAsync(connection, transaction, "monitor-append", stoppingToken);
                    foreach (var item in batch)
                    {
                        var r = item.Record;
                        await using var command = OperationalDatabase.Command(connection, """
                            IF NOT EXISTS(SELECT 1 FROM fabrOps.MonitorRecord WHERE RecordId=@id AND Kind=@kind)
                            INSERT fabrOps.MonitorRecord(RecordId,Kind,AgentHandle,TraceId,Channel,ExecutionCategory,Timestamp,Payload)
                            VALUES(@id,@kind,@agent,@trace,@channel,@category,@time,@payload);
                            """, transaction, ("@id", r.Id), ("@kind", r.Kind), ("@agent", r.AgentHandle), ("@trace", r.TraceId),
                            ("@channel", r.Channel), ("@category", r.ExecutionCategory), ("@time", r.Timestamp), ("@payload", r.Payload?.GetRawText()));
                        await command.ExecuteNonQueryAsync(stoppingToken);
                    }
                    await transaction.CommitAsync(stoppingToken);
                    Interlocked.Add(ref queuedBytes, -batch.Sum(b => (long)b.Bytes)); Interlocked.Add(ref queuedRecords, -batch.Count);
                    batch.Clear(); lastWrite = DateTimeOffset.UtcNow; failures = 0;
                }
                if (DateTimeOffset.UtcNow >= cleanupAt)
                {
                    await using var connection = await database.OpenAsync(stoppingToken);
                    await using var cleanup = OperationalDatabase.Command(connection,
                        "DELETE TOP(1000) FROM fabrOps.MonitorRecord WHERE Timestamp < @cutoff;", null, ("@cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays)));
                    await cleanup.ExecuteNonQueryAsync(stoppingToken);
                    cleanupAt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedWrites); logger.LogWarning(ex, "SQL monitoring write failed; retained batch will retry");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
    public async Task<MonitorPage> QueryAsync(MonitorQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var identity = OperationalDatabase.Command(connection, "SELECT CONVERT(nvarchar(36),StoreId) FROM fabrOps.MonitorStore WHERE Id=1;"))
            source = (string?)await identity.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Monitor schema is unavailable.");
        var cursor = MonitorQueryEngine.Decode(query.Cursor, source);
        if (cursor.Filter is not null && cursor.Filter != MonitorQueryEngine.Filter(query)) throw new ArgumentException("Monitor filters changed; restart pagination.");
        if (cursor.Source != source) return new() { SourceId = source, DataScope = "cluster", Gap = true, Notice = "Storage identity changed; restart query." };
        await using var bounds = OperationalDatabase.Command(connection, "SELECT ISNULL(MIN(Sequence),0),ISNULL(MAX(Sequence),0) FROM fabrOps.MonitorRecord;");
        long low, high;
        await using (var reader = await bounds.ExecuteReaderAsync(cancellationToken)) { await reader.ReadAsync(cancellationToken); low = reader.GetInt64(0); high = reader.GetInt64(1); }
        if (cursor.After != 0 && low > cursor.After + 1) return new() { SourceId = source, DataScope = "cluster", Gap = true, Notice = "Requested records expired; restart query." };
        high = cursor.High == 0 ? high : cursor.High;
        await using var command = OperationalDatabase.Command(connection, """
            SELECT TOP(@limit) Sequence,RecordId,Kind,AgentHandle,TraceId,Channel,ExecutionCategory,Timestamp,CASE WHEN @payload=1 THEN Payload ELSE NULL END
            FROM fabrOps.MonitorRecord WHERE Sequence>@after AND Sequence<=@high
            AND (@kind IS NULL OR Kind=@kind) AND (@agent IS NULL OR AgentHandle=@agent)
            AND (@principal IS NULL OR LEFT(AgentHandle,LEN(@principal)+1)=@principal+':')
            AND (@trace IS NULL OR TraceId=@trace) AND (@channel IS NULL OR Channel=@channel)
            AND (@category IS NULL OR ExecutionCategory=@category) AND (@from IS NULL OR Timestamp>=@from) AND (@to IS NULL OR Timestamp<=@to)
            ORDER BY Sequence;
            """, null, ("@limit", Math.Clamp(query.Limit, 1, 1000) + 1), ("@after", cursor.After), ("@high", high), ("@payload", query.IncludePayload),
            ("@kind", query.Kind), ("@agent", query.AgentHandle), ("@principal", query.Principal), ("@trace", query.TraceId), ("@channel", query.Channel),
            ("@category", query.ExecutionCategory), ("@from", query.From), ("@to", query.To));
        var records = new List<MonitorRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) records.Add(new() { Sequence = reader.GetInt64(0), Id = reader.GetString(1), Kind = reader.GetString(2),
                AgentHandle = reader.IsDBNull(3) ? null : reader.GetString(3), TraceId = reader.IsDBNull(4) ? null : reader.GetString(4), Channel = reader.IsDBNull(5) ? null : reader.GetString(5),
                ExecutionCategory = reader.GetString(6), Timestamp = reader.GetFieldValue<DateTimeOffset>(7), Payload = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(8)) });
        }
        query.Cursor = MonitorQueryEngine.Encode(new(source, cursor.After, high, cursor.Filter));
        return MonitorQueryEngine.Page(records, query, source, "cluster");
    }
    public async Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        string? persistenceError = null;
        try
        {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = OperationalDatabase.Command(connection, "SELECT CONVERT(nvarchar(36),StoreId) FROM fabrOps.MonitorStore WHERE Id=1;");
        source = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { persistenceError = "Operational monitoring storage is unavailable."; }
        return JsonSerializer.SerializeToElement(new { provider = "sql", sourceId = source, dataScope = "cluster", retentionDays,
            queuedRecords = Interlocked.Read(ref queuedRecords), queuedBytes = Interlocked.Read(ref queuedBytes),
            droppedRecords = Interlocked.Read(ref dropped), failedWrites = Interlocked.Read(ref failedWrites), lastWrite,
            payloadsCaptured = LlmCaptureOptions.CapturePayloads, durableAfterFlush = true, persistenceError });
    }
    public async Task<string?> ReadPayloadAsync(long sequence, int offset, int count, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = OperationalDatabase.Command(connection, "SELECT SUBSTRING(Payload,@offset,@count) FROM fabrOps.MonitorRecord WHERE Sequence=@sequence;", null,
            ("@offset", Math.Max(0, offset) + 1), ("@count", Math.Clamp(count, 1, 32000)), ("@sequence", sequence));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    internal const string Schema = """
        IF OBJECT_ID('fabrOps.MonitorStore') IS NULL
        BEGIN
          CREATE TABLE fabrOps.MonitorStore(Id int NOT NULL PRIMARY KEY CHECK(Id=1), StoreId uniqueidentifier NOT NULL);
          INSERT fabrOps.MonitorStore VALUES(1,NEWID());
          CREATE TABLE fabrOps.MonitorRecord(Sequence bigint IDENTITY PRIMARY KEY,RecordId nvarchar(128) NOT NULL,Kind nvarchar(16) NOT NULL,
            AgentHandle nvarchar(512) NULL,TraceId nvarchar(128) NULL,Channel nvarchar(256) NULL,ExecutionCategory nvarchar(16) NOT NULL,
            Timestamp datetimeoffset NOT NULL,Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1),UNIQUE(RecordId,Kind));
          CREATE INDEX IX_Monitor_Agent ON fabrOps.MonitorRecord(AgentHandle,Sequence);
          CREATE INDEX IX_Monitor_Trace ON fabrOps.MonitorRecord(TraceId,Sequence);
          CREATE INDEX IX_Monitor_Time ON fabrOps.MonitorRecord(Timestamp,Sequence);
        END
        """;
}
