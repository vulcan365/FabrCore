using System.Collections.Concurrent;
using System.Threading.Channels;
using FabrCore.Core;
using FabrCore.Host.Services;
using FabrCore.Host.A2A.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>Everything needed to run one A2A turn.</summary>
internal sealed record A2AExecutionRequest(
    A2AExposedAgent Agent,
    string PrincipalHandle,
    A2AMessage Message,
    string TaskId,
    string ContextId,
    string? Caller);

/// <summary>Runs A2A turns as tasks and publishes their lifecycle events.</summary>
internal interface IA2ATaskExecutor
{
    /// <summary>Starts a turn. The returned execution is already running.</summary>
    A2ATaskExecution Start(A2AExecutionRequest request);

    /// <summary>Returns the still-live execution for a task, or null once it has been released.</summary>
    A2ATaskExecution? Find(string taskId);

    /// <summary>Reads a task back, from the live execution when there is one and the store otherwise.</summary>
    ValueTask<A2ATask?> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken);

    /// <summary>Requests cancellation. Returns false when the task is unknown or already terminal.</summary>
    ValueTask<A2ACancelResult> CancelAsync(string taskId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a cancellation request.</summary>
internal enum A2ACancelOutcome
{
    Canceled,
    NotFound,
    NotCancelable,
}

/// <summary>Result of <see cref="IA2ATaskExecutor.CancelAsync"/>.</summary>
internal readonly record struct A2ACancelResult(A2ACancelOutcome Outcome, A2ATask? Task);

internal sealed class A2ATaskExecutor : IA2ATaskExecutor
{
    private readonly IFabrCoreAgentService _agentService;
    private readonly IA2AAgentProvisioner _provisioner;
    private readonly IA2ATaskStore _taskStore;
    private readonly A2AOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<A2ATaskExecutor> _logger;
    private readonly ConcurrentDictionary<string, A2ATaskExecution> _live = new();

    public A2ATaskExecutor(
        IFabrCoreAgentService agentService,
        IA2AAgentProvisioner provisioner,
        IA2ATaskStore taskStore,
        IOptions<A2AOptions> options,
        TimeProvider timeProvider,
        ILogger<A2ATaskExecutor> logger)
    {
        _agentService = agentService;
        _provisioner = provisioner;
        _taskStore = taskStore;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public A2ATaskExecution Start(A2AExecutionRequest request)
    {
        var execution = new A2ATaskExecution(request, _timeProvider);
        _live[request.TaskId] = execution;
        TrimFinishedExecutions();
        execution.Run(RunAsync);
        return execution;
    }

    /// <summary>
    /// Finished executions linger briefly so a late <c>tasks/resubscribe</c> still finds their
    /// recorded events. Under sustained load that window would otherwise let the live map grow
    /// without bound, so release the oldest finished ones early once it is over capacity.
    /// </summary>
    private void TrimFinishedExecutions()
    {
        if (_live.Count <= _options.Tasks.MaxRetainedTasks)
        {
            return;
        }

        foreach (var (id, execution) in _live
                     .Where(kvp => kvp.Value.Completion.IsCompleted)
                     .OrderBy(kvp => kvp.Value.FinishedAt ?? DateTimeOffset.MaxValue)
                     .Take(_live.Count - _options.Tasks.MaxRetainedTasks))
        {
            if (_live.TryRemove(id, out _))
            {
                execution.Dispose();
            }
        }
    }

    public A2ATaskExecution? Find(string taskId)
        => _live.TryGetValue(taskId, out var execution) ? execution : null;

    public async ValueTask<A2ATask?> GetTaskAsync(
        string taskId, int? historyLength, CancellationToken cancellationToken)
    {
        var task = Find(taskId)?.Snapshot() ?? await _taskStore.GetAsync(taskId, cancellationToken);
        return task is null ? null : TrimHistory(task, historyLength ?? _options.Tasks.DefaultHistoryLength);
    }

    public async ValueTask<A2ACancelResult> CancelAsync(string taskId, CancellationToken cancellationToken)
    {
        var execution = Find(taskId);
        if (execution is null)
        {
            var stored = await _taskStore.GetAsync(taskId, cancellationToken);
            return stored is null
                ? new A2ACancelResult(A2ACancelOutcome.NotFound, null)
                : new A2ACancelResult(A2ACancelOutcome.NotCancelable, stored);
        }

        if (A2ATaskStates.IsTerminal(execution.Snapshot().Status.State))
        {
            return new A2ACancelResult(A2ACancelOutcome.NotCancelable, execution.Snapshot());
        }

        execution.RequestCancellation();

        // Give the run loop a moment to settle into the canceled state so the caller sees the
        // terminal task rather than the one it had before asking.
        try
        {
            await execution.Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            // The agent call is not honoring cancellation; report the current state anyway.
        }
        catch (OperationCanceledException)
        {
            // The HTTP request went away — the task state below is still the honest answer.
        }

        return new A2ACancelResult(A2ACancelOutcome.Canceled, execution.Snapshot());
    }

    private async Task RunAsync(A2ATaskExecution execution)
    {
        var request = execution.Request;
        var cancellationToken = execution.CancellationToken;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Tasks.ExecutionTimeout);

            execution.SetStatus(A2ATaskStates.Working);

            var handle = await _provisioner.EnsureAgentAsync(
                request.Agent, request.PrincipalHandle, request.ContextId, timeout.Token);

            var agentMessage = A2AMessageTranslator.ToAgentMessage(
                request.Message,
                request.Agent,
                request.TaskId,
                request.ContextId,
                request.Caller,
                _options.Interop);

            AgentMessage reply;
            try
            {
                reply = await _agentService
                    .SendAndReceiveMessageAsync(request.PrincipalHandle, handle, agentMessage)
                    .WaitAsync(timeout.Token);
            }
            catch
            {
                // The agent may have been evicted since we cached the ensure result; make the next
                // request re-provision rather than failing the same way forever.
                _provisioner.Invalidate(request.PrincipalHandle, handle);
                throw;
            }

            if (reply?.MessageType == SystemMessageTypes.Error)
            {
                execution.Fail(reply.Message ?? "The agent reported an error.");
                return;
            }

            execution.AddArtifact(A2AMessageTranslator.ToArtifact(reply, request.Agent));
            execution.Complete(reply?.Message ?? string.Empty);
        }
        catch (OperationCanceledException) when (execution.CancellationRequested)
        {
            execution.Cancel();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "A2A task {TaskId} for agent {Agent} timed out after {Timeout}.",
                request.TaskId, request.Agent.Name, _options.Tasks.ExecutionTimeout);
            execution.Fail($"The agent did not respond within {_options.Tasks.ExecutionTimeout}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "A2A task {TaskId} for agent {Agent} failed.", request.TaskId, request.Agent.Name);
            execution.Fail(ex.Message);
        }
        finally
        {
            await _taskStore.SaveAsync(execution.Snapshot(), CancellationToken.None);
            execution.CloseSubscribers();

            // Keep the finished execution addressable for a short window so an in-flight
            // tasks/resubscribe still finds its recorded events, then fall back to the store.
            _ = ReleaseLaterAsync(execution);
        }
    }

    private async Task ReleaseLaterAsync(A2ATaskExecution execution)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A2A execution release delay ended early for task {TaskId}.", execution.Request.TaskId);
        }

        _live.TryRemove(execution.Request.TaskId, out _);
        execution.Dispose();
    }

    private static A2ATask TrimHistory(A2ATask task, int historyLength)
    {
        if (task.History is null || historyLength < 0 || task.History.Count <= historyLength)
        {
            return task;
        }

        return new A2ATask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = task.Status,
            Artifacts = task.Artifacts,
            Metadata = task.Metadata,
            History = task.History.TakeLast(historyLength).ToList(),
        };
    }
}

/// <summary>
/// A single running A2A task: its current snapshot, the events it has emitted, and the
/// subscribers streaming them.
/// </summary>
internal sealed class A2ATaskExecution : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<object> _events = new();
    private readonly List<Channel<object>> _subscribers = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TimeProvider _timeProvider;
    private readonly A2ATask _task;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closed;

    public A2ATaskExecution(A2AExecutionRequest request, TimeProvider timeProvider)
    {
        Request = request;
        _timeProvider = timeProvider;

        var userTurn = request.Message;
        userTurn.TaskId = request.TaskId;
        userTurn.ContextId = request.ContextId;

        _task = new A2ATask
        {
            Id = request.TaskId,
            ContextId = request.ContextId,
            Status = new A2ATaskStatus { State = A2ATaskStates.Submitted, Timestamp = Now(timeProvider) },
            History = new List<A2AMessage> { userTurn },
            Artifacts = new List<A2AArtifact>(),
        };

        // The first event of an A2A stream is the task itself, so a client that subscribes late
        // still learns the id and context it is following.
        _events.Add(Snapshot());
    }

    public A2AExecutionRequest Request { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool CancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>Completes when the run loop has finished and the terminal event has been emitted.</summary>
    public Task Completion => _completion.Task;

    /// <summary>When the run loop finished, or null while it is still running.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    internal void Run(Func<A2ATaskExecution, Task> body)
        => _ = Task.Run(async () =>
        {
            try
            {
                await body(this);
            }
            finally
            {
                FinishedAt = _timeProvider.GetUtcNow();
                _completion.TrySetResult();
            }
        });

    /// <summary>Returns an immutable copy of the current task state.</summary>
    public A2ATask Snapshot()
    {
        lock (_gate)
        {
            return new A2ATask
            {
                Id = _task.Id,
                ContextId = _task.ContextId,
                Status = new A2ATaskStatus
                {
                    State = _task.Status.State,
                    Message = _task.Status.Message,
                    Timestamp = _task.Status.Timestamp,
                },
                Artifacts = _task.Artifacts is null ? null : new List<A2AArtifact>(_task.Artifacts),
                History = _task.History is null ? null : new List<A2AMessage>(_task.History),
                Metadata = _task.Metadata,
            };
        }
    }

    public void RequestCancellation()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already released; the task is terminal and there is nothing left to cancel.
        }
    }

    public void SetStatus(string state, A2AMessage? message = null, bool final = false)
    {
        lock (_gate)
        {
            _task.Status = new A2ATaskStatus
            {
                State = state,
                Message = message,
                Timestamp = Now(_timeProvider),
            };

            if (message is not null)
            {
                (_task.History ??= new List<A2AMessage>()).Add(message);
            }
        }

        Publish(new A2ATaskStatusUpdateEvent
        {
            TaskId = _task.Id,
            ContextId = _task.ContextId,
            Status = Snapshot().Status,
            Final = final,
        });
    }

    public void AddArtifact(A2AArtifact artifact)
    {
        lock (_gate)
        {
            (_task.Artifacts ??= new List<A2AArtifact>()).Add(artifact);
        }

        Publish(new A2ATaskArtifactUpdateEvent
        {
            TaskId = _task.Id,
            ContextId = _task.ContextId,
            Artifact = artifact,
            Append = false,
            LastChunk = true,
        });
    }

    public void Complete(string text)
        => SetStatus(
            A2ATaskStates.Completed,
            A2AMessageTranslator.ToA2AMessage(text, _task.Id, _task.ContextId),
            final: true);

    public void Fail(string reason)
        => SetStatus(
            A2ATaskStates.Failed,
            A2AMessageTranslator.ToA2AMessage(reason, _task.Id, _task.ContextId),
            final: true);

    public void Cancel()
        => SetStatus(
            A2ATaskStates.Canceled,
            A2AMessageTranslator.ToA2AMessage("The task was canceled.", _task.Id, _task.ContextId),
            final: true);

    /// <summary>
    /// Subscribes to this task's events. Everything already emitted is replayed first, so a
    /// subscriber never misses the terminal event because it arrived a moment too late.
    /// </summary>
    public IAsyncEnumerable<object> SubscribeAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_gate)
        {
            foreach (var recorded in _events)
            {
                channel.Writer.TryWrite(recorded);
            }

            if (_closed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void CloseSubscribers()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    public void Dispose()
    {
        CloseSubscribers();
        _cancellation.Dispose();
    }

    private void Publish(object evt)
    {
        lock (_gate)
        {
            _events.Add(evt);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(evt);
            }
        }
    }

    private static string Now(TimeProvider timeProvider)
        => timeProvider.GetUtcNow().ToString("O");
}
