using System.Diagnostics;
using System.Runtime.CompilerServices;
using FabrCore.Core.Monitoring;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>Adds bounded execution and FabrCore attribution to a private in-process agent.</summary>
internal sealed class BoundedInternalAgent : DelegatingAIAgent, IAsyncDisposable
{
    private readonly string ownerHandle;
    private readonly string internalName;
    private readonly InternalAgentExecutionPolicy policy;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim proxyGate;
    private readonly SemaphoreSlim agentGate;
    private readonly IAgentMessageMonitor? monitor;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifetimeSource = new();
    private readonly TaskCompletionSource allRunsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifetimeLock = new();
    private int activeRuns;
    private int disposed;

    public BoundedInternalAgent(
        AIAgent innerAgent,
        string ownerHandle,
        InternalAgentExecutionPolicy policy,
        TimeSpan timeout,
        int maxConcurrency,
        SemaphoreSlim proxyGate,
        TimeProvider timeProvider,
        IAgentMessageMonitor? monitor,
        ILogger logger)
        : base(innerAgent)
    {
        this.ownerHandle = ownerHandle;
        internalName = innerAgent.Name!;
        this.policy = policy;
        this.timeout = timeout;
        this.proxyGate = proxyGate;
        this.timeProvider = timeProvider;
        this.monitor = monitor;
        this.logger = logger;
        agentGate = new SemaphoreSlim(
            policy == InternalAgentExecutionPolicy.ConcurrentReadOnly ? maxConcurrency : 1,
            policy == InternalAgentExecutionPolicy.ConcurrentReadOnly ? maxConcurrency : 1);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        BeginRun();
        var started = timeProvider.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, lifetimeSource.Token);
        var acquiredProxy = false;
        var acquiredAgent = false;

        try
        {
            await agentGate.WaitAsync(linked.Token);
            acquiredAgent = true;
            await proxyGate.WaitAsync(linked.Token);
            acquiredProxy = true;

            await RecordAsync("started", started);
            using var attribution = LlmCallContext.Begin(ownerHandle, $"InternalAgent:{internalName}");
            var response = await InnerAgent.RunAsync(messages, session, options, linked.Token);
            await RecordAsync("completed", started);
            return response;
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await RecordAsync("timed-out", started, ex);
            throw new TimeoutException($"Internal agent '{internalName}' exceeded its {timeout.TotalSeconds:0.###}-second execution timeout.", ex);
        }
        catch (OperationCanceledException ex)
        {
            await RecordAsync("cancelled", started, ex);
            throw;
        }
        catch (Exception ex)
        {
            await RecordAsync("failed", started, ex);
            throw;
        }
        finally
        {
            if (acquiredProxy) proxyGate.Release();
            if (acquiredAgent) agentGate.Release();
            EndRun();
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        BeginRun();
        var started = timeProvider.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, lifetimeSource.Token);
        var acquiredProxy = false;
        var acquiredAgent = false;
        var completed = false;

        try
        {
            await agentGate.WaitAsync(linked.Token);
            acquiredAgent = true;
            await proxyGate.WaitAsync(linked.Token);
            acquiredProxy = true;

            await RecordAsync("started", started);
            using var attribution = LlmCallContext.Begin(ownerHandle, $"InternalAgent:{internalName}");
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, linked.Token))
            {
                yield return update;
            }
            completed = true;
        }
        finally
        {
            if (completed)
            {
                await RecordAsync("completed", started);
            }
            else if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await RecordAsync("timed-out", started);
            }
            else if (cancellationToken.IsCancellationRequested || lifetimeSource.IsCancellationRequested)
            {
                await RecordAsync("cancelled", started);
            }
            else
            {
                await RecordAsync("failed", started);
            }

            if (acquiredProxy) proxyGate.Release();
            if (acquiredAgent) agentGate.Release();
            EndRun();
        }
    }

    private async Task RecordAsync(string status, long started, Exception? error = null)
    {
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.RecordEventAsync(new MonitoredEvent
            {
                AgentHandle = ownerHandle,
                Type = $"internal-agent.task.{status}",
                Source = "FabrCore.Sdk",
                Subject = internalName,
                EventTime = timeProvider.GetUtcNow(),
                TraceId = LlmCallContext.Current?.TraceId ?? Activity.Current?.TraceId.ToString(),
                Args = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["internal_agent.name"] = internalName,
                    ["delegation.kind"] = "internal",
                    ["delegation.parent"] = ownerHandle,
                    ["execution.policy"] = policy.ToString(),
                    ["duration_ms"] = timeProvider.GetElapsedTime(started).TotalMilliseconds.ToString("0.###"),
                    ["error"] = error?.GetType().Name ?? string.Empty
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record internal-agent monitor event {Status} for {InternalAgent}", status, internalName);
        }
    }

    private void BeginRun()
    {
        lock (lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            activeRuns++;
        }
    }

    private void EndRun()
    {
        lock (lifetimeLock)
        {
            activeRuns--;
            if (disposed != 0 && activeRuns == 0)
            {
                allRunsCompleted.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (lifetimeLock)
        {
            if (disposed != 0) return;
            disposed = 1;
            if (activeRuns == 0) allRunsCompleted.TrySetResult();
        }

        lifetimeSource.Cancel();
        await allRunsCompleted.Task;
        agentGate.Dispose();
        lifetimeSource.Dispose();
    }
}
