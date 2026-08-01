using System.Collections.Concurrent;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Factory for scope-bound memory service instances.
/// Caches instances per scope key using a ConcurrentDictionary — agents configured
/// with the same shared scope receive the same instance.
/// </summary>
internal class AgentMemoryProvider : IAgentMemoryProvider
{
    private readonly ConcurrentDictionary<string, IAgentMemoryService> _services = new();
    private readonly IMemoryStore _store;
    private readonly IMemoryIndexManager _indexManager;
    private readonly IMemoryRetriever _retriever;
    private readonly IMemoryCompactor _compactor;
    private readonly IRetrievalPlanner _planner;
    private readonly IMemorySummaryTree _summaryTree;
    private readonly IMemoryScopeService _scopeService;
    private readonly IMemoryAuditLog _auditLog;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;

    public AgentMemoryProvider(
        IMemoryStore store,
        IMemoryIndexManager indexManager,
        IMemoryRetriever retriever,
        IMemoryCompactor compactor,
        IRetrievalPlanner planner,
        IMemorySummaryTree summaryTree,
        IMemoryScopeService scopeService,
        IMemoryAuditLog auditLog,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _indexManager = indexManager;
        _retriever = retriever;
        _compactor = compactor;
        _planner = planner;
        _summaryTree = summaryTree;
        _scopeService = scopeService;
        _auditLog = auditLog;
        _options = options;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public IAgentMemoryService GetMemoryService(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        return _services.GetOrAdd(scopeKey.Trim(), key =>
            new AgentMemoryService(
                key,
                _store,
                _indexManager,
                _retriever,
                _compactor,
                _planner,
                _summaryTree,
                _scopeService,
                _auditLog,
                _options,
                _serviceProvider,
                _loggerFactory));
    }

    public bool EvictMemoryService(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return _services.TryRemove(scopeKey.Trim(), out _);
    }
}
