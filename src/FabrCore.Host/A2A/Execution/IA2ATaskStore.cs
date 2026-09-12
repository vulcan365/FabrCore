using System.Collections.Concurrent;
using FabrCore.Host.A2A.Protocol;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Persistence for A2A tasks so <c>tasks/get</c> can answer after the request that created the
/// task has finished.
/// </summary>
/// <remarks>
/// SQL-mode Host registration uses durable shared snapshots; standalone uses per-process memory.
/// Register a custom singleton before Host registration to replace the built-in store.
/// </remarks>
public interface IA2ATaskStore
{
    /// <summary>Returns the stored task, or null when it is unknown or has aged out.</summary>
    ValueTask<A2ATask?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces a task snapshot.</summary>
    ValueTask SaveAsync(A2ATask task, CancellationToken cancellationToken = default);

    /// <summary>Lists retained snapshots. Custom stores should override to support ListTasks.</summary>
    ValueTask<IReadOnlyList<A2ATask>> ListAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This task store does not support listing.");
}

/// <summary>Coordinates active executions across hosts without replaying uncertain external effects.</summary>
public interface IDurableA2ATaskStore
{
    TimeSpan HeartbeatInterval { get; }
    ValueTask<bool> RequestCancellationAsync(string taskId, CancellationToken ct = default);
    ValueTask<bool> IsCancellationRequestedAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// In-memory task store bounded by <see cref="A2ATaskOptions.MaxRetainedTasks"/> and
/// <see cref="A2ATaskOptions.Retention"/>.
/// </summary>
internal sealed class InMemoryA2ATaskStore : IA2ATaskStore
{
    private readonly A2ATaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Entry> _tasks = new();

    public InMemoryA2ATaskStore(IOptions<A2AOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value.Tasks;
        _timeProvider = timeProvider;
    }

    public ValueTask<A2ATask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var entry))
        {
            return ValueTask.FromResult<A2ATask?>(null);
        }

        if (IsExpired(entry))
        {
            _tasks.TryRemove(taskId, out _);
            return ValueTask.FromResult<A2ATask?>(null);
        }

        return ValueTask.FromResult<A2ATask?>(entry.Task);
    }

    public ValueTask SaveAsync(A2ATask task, CancellationToken cancellationToken = default)
    {
        _tasks[task.Id] = new Entry(task, _timeProvider.GetUtcNow());
        Trim();
        return ValueTask.CompletedTask;
    }

    private bool IsExpired(Entry entry)
        => A2ATaskStates.IsTerminal(entry.Task.Status.State)
           && _timeProvider.GetUtcNow() - entry.SavedAt > _options.Retention;

    public ValueTask<IReadOnlyList<A2ATask>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<A2ATask>>(_tasks.Values
            .Where(entry => !IsExpired(entry)).Select(entry => entry.Task).ToList());

    private void Trim()
    {
        if (_tasks.Count <= _options.MaxRetainedTasks)
        {
            return;
        }

        // Drop aged-out entries first, then the oldest terminal tasks. Tasks still running are
        // never evicted: a client holding their id is entitled to read them back.
        foreach (var (id, entry) in _tasks)
        {
            if (IsExpired(entry))
            {
                _tasks.TryRemove(id, out _);
            }
        }

        var excess = _tasks.Count - _options.MaxRetainedTasks;
        if (excess <= 0)
        {
            return;
        }

        foreach (var (id, _) in _tasks
                     .Where(kvp => A2ATaskStates.IsTerminal(kvp.Value.Task.Status.State))
                     .OrderBy(kvp => kvp.Value.SavedAt)
                     .Take(excess))
        {
            _tasks.TryRemove(id, out _);
        }
    }

    private readonly record struct Entry(A2ATask Task, DateTimeOffset SavedAt);
}
