using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Sdk.Memory;

/// <summary>
/// Creates the agent-facing memory tools (search, write, get).
/// </summary>
public interface IMemoryToolFactory
{
    IList<AITool> CreateTools(string agentHandle);
}

/// <summary>
/// Factory that creates memory_search, memory_write, and memory_get AI tools
/// for agents to interact with persistent memory.
/// </summary>
public sealed class MemoryToolFactory : IMemoryToolFactory
{
    private readonly IMemoryStore _memoryStore;
    private readonly IEmbeddings _embeddings;
    private readonly MemoryOptions _options;
    private readonly ILogger<MemoryToolFactory> _logger;

    public MemoryToolFactory(
        IMemoryStore memoryStore,
        IEmbeddings embeddings,
        IOptions<MemoryOptions> options,
        ILogger<MemoryToolFactory> logger)
    {
        _memoryStore = memoryStore;
        _embeddings = embeddings;
        _options = options.Value;
        _logger = logger;
    }

    public IList<AITool> CreateTools(string agentHandle)
    {
        return
        [
            AIFunctionFactory.Create(
                async ([Description("The search query to find relevant information in memory")] string query,
                       [Description("Maximum number of results to return (default 6)")] int maxResults = 6) =>
                {
                    _logger.LogDebug("memory_search: query='{Query}', maxResults={MaxResults}, agent={Agent}",
                        query, maxResults, agentHandle);

                    var embedding = await _embeddings.GetEmbeddings(query);
                    var results = await _memoryStore.SearchAsync(embedding.Vector.ToArray(), query, maxResults);

                    if (results.Count == 0)
                        return "No relevant information found in memory.";

                    return string.Join("\n\n---\n\n", results.Select((r, i) =>
                        $"**Result {i + 1}** (score: {r.Score:F3}, source: {r.SourcePath})\n{r.Content}"));
                },
                "memory_search",
                "Search persistent memory for relevant information. Use this to find past context, decisions, preferences, or facts."),

            AIFunctionFactory.Create(
                async ([Description("The content to write to memory")] string content,
                       [Description("Source path for the memory entry (default: daily log)")] string? source = null,
                       [Description("Optional title for the memory entry")] string? title = null) =>
                {
                    source ??= $"memory/{DateTimeOffset.UtcNow:yyyy-MM-dd}.md";

                    _logger.LogDebug("memory_write: source='{Source}', title='{Title}', contentLength={Length}, agent={Agent}",
                        source, title, content.Length, agentHandle);

                    // Delete existing chunks for this source to allow overwrite
                    await _memoryStore.DeleteBySourceAsync(source);

                    var chunks = TextChunker.ChunkText(content, _options.ChunkSize, _options.ChunkOverlap);
                    var records = new List<MemoryChunk>();

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var embedding = await _embeddings.GetEmbeddings(chunks[i]);
                        records.Add(new MemoryChunk
                        {
                            Content = chunks[i],
                            SourcePath = source,
                            SourceType = source.Contains("MEMORY.md", StringComparison.OrdinalIgnoreCase) ? "longterm" : "daily",
                            Title = title,
                            ChunkIndex = i,
                            Embedding = embedding.Vector.ToArray()
                        });
                    }

                    if (records.Count > 0)
                    {
                        await _memoryStore.WriteBatchAsync(records);
                    }

                    _logger.LogInformation("Wrote {ChunkCount} chunks to memory source '{Source}'", records.Count, source);
                    return $"Wrote {records.Count} chunk(s) to memory at '{source}'.";
                },
                "memory_write",
                "Write content to persistent memory. Use this to save important decisions, preferences, facts, or context for future reference. Write to 'MEMORY.md' for long-term durable facts, or let it default to the daily log."),

            AIFunctionFactory.Create(
                async ([Description("The source path to read (e.g., 'MEMORY.md' or 'memory/2026-02-14.md')")] string source) =>
                {
                    _logger.LogDebug("memory_get: source='{Source}', agent={Agent}", source, agentHandle);

                    var results = await _memoryStore.GetBySourceAsync(source);

                    if (results.Count == 0)
                        return $"No memory found at source '{source}'.";

                    // Reassemble chunks in order
                    return string.Join("\n\n", results.OrderBy(r => r.ChunkIndex).Select(r => r.Content));
                },
                "memory_get",
                "Read a specific memory source by its path. Use this to review what's already stored at a particular location.")
        ];
    }
}
