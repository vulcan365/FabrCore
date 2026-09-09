using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Services;

/// <summary>Reads own and core memory; all mutations and extraction stay in own memory.</summary>
internal sealed class LayeredMemoryService(IAgentMemoryService own, IAgentMemoryService core,
    AgentMemoryOptions options) : IAgentMemoryService
{
    public string ScopeKey => own.ScopeKey;
    public Task<MemoryEntry> SaveMemoryAsync(string title, MemoryType type, string content,
        string? description = null, Dictionary<string,string>? metadata = null, bool isPointInTime = false,
        CancellationToken ct = default) => own.SaveMemoryAsync(title, type, content, description, metadata, isPointInTime, ct);
    public Task<MemoryEntry> UpdateMemoryAsync(Guid memoryId, string? title = null, MemoryType? type = null,
        string? content = null, string? description = null, MemoryTemperature? temperature = null,
        CancellationToken ct = default) => own.UpdateMemoryAsync(memoryId, title, type, content, description, temperature, ct);
    public Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default) => own.ForgetMemoryAsync(memoryId, ct);
    public Task<MemoryConsolidationResult> ConsolidateAsync(CancellationToken ct = default) => own.ConsolidateAsync(ct);
    public Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(IList<ChatMessage> messages, CancellationToken ct = default)
        => own.ExtractMemoriesAsync(messages, ct);
    public string FormatRecallContext(MemoryRecallResult recall) => own.FormatRecallContext(recall);
    public async Task<MemoryIndex> GetMemoryIndexAsync(CancellationToken ct = default)
        => MergeIndex(await own.GetMemoryIndexAsync(ct), await core.GetMemoryIndexAsync(ct));
    private MemoryIndex MergeIndex(MemoryIndex first, MemoryIndex second)
    {
        var result = new MemoryIndex();
        foreach (var entry in first.Entries.Concat(second.Entries).DistinctBy(e => e.MemoryId))
        {
            if (result.Entries.Count >= options.HotIndex.MaxEntries) break;
            result.Entries.Add(entry);
            result.RecalculateTokens();
            if (result.TotalEstimatedTokens > options.HotIndex.MaxTokens) result.Entries.RemoveAt(result.Entries.Count - 1);
        }
        result.RecalculateTokens();
        return result;
    }
    public async Task<MemoryRecallResult> RecallAsync(string query, IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default)
    {
        var a = await own.RecallAsync(query, alreadySurfacedIds, ct);
        var b = await core.RecallAsync(query, alreadySurfacedIds, ct);
        return new MemoryRecallResult {
            HotIndex = MergeIndex(a.HotIndex, b.HotIndex),
            WarmMemories = a.WarmMemories.Concat(b.WarmMemories).DistinctBy(e => e.Id).Take(options.Retrieval.WarmRetrievalLimit).ToList(),
            ArchiveResults = a.ArchiveResults.Concat(b.ArchiveResults).DistinctBy(e => e.Entry.Id).OrderBy(e => e.Distance).Take(options.Retrieval.WarmRetrievalLimit).ToList(),
            SummaryNodes = a.SummaryNodes.Concat(b.SummaryNodes).Take(options.Retrieval.WarmRetrievalLimit).ToList(),
            FreshnessWarnings = a.FreshnessWarnings.Concat(b.FreshnessWarnings).Distinct().ToList()
        };
    }
    public async Task<IReadOnlyList<MemorySearchResult>> SearchArchiveAsync(string query, int limit = 10,
        MemoryType? typeFilter = null, CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var a = await own.SearchArchiveAsync(query, limit, typeFilter, ct);
        var b = await core.SearchArchiveAsync(query, limit, typeFilter, ct);
        return a.Concat(b).DistinctBy(e => e.Entry.Id).OrderBy(e => e.Distance).Take(limit).ToList();
    }
}
