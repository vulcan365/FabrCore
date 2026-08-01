using System.Threading.Channels;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed record GraphRagIngestionJob(
    string FileName,
    string ScopeKey,
    string MarkdownContent);

public sealed class GraphRagIngestionQueue
{
    private readonly Channel<GraphRagIngestionJob> _channel =
        Channel.CreateUnbounded<GraphRagIngestionJob>();

    public ValueTask EnqueueAsync(GraphRagIngestionJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<GraphRagIngestionJob> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

public sealed class GraphRagIngestionWorker : BackgroundService
{
    private readonly GraphRagIngestionQueue _queue;
    private readonly IKnowledgeIngestionService _ingestion;
    private readonly ILogger<GraphRagIngestionWorker> _logger;

    public GraphRagIngestionWorker(
        GraphRagIngestionQueue queue,
        IKnowledgeIngestionService ingestion,
        ILogger<GraphRagIngestionWorker> logger)
    {
        _queue = queue;
        _ingestion = ingestion;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await _ingestion.IngestDocumentAsync(
                    job.FileName,
                    job.ScopeKey,
                    job.MarkdownContent,
                    stoppingToken);

                _logger.LogInformation(
                    "Ingested GraphRAG document {DocumentId} in scope {ScopeKey} with status {Status}",
                    result.DocumentId,
                    result.ScopeKey,
                    result.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to ingest GraphRAG document {FileName} in scope {ScopeKey}",
                    job.FileName,
                    job.ScopeKey);
            }
        }
    }
}

// Startup registration:
// builder.Services.AddSingleton<GraphRagIngestionQueue>();
// builder.Services.AddHostedService<GraphRagIngestionWorker>();
