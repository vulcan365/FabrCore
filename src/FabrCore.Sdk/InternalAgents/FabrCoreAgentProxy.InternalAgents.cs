using FabrCore.Core.Monitoring;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

public abstract partial class FabrCoreAgentProxy
{
    private const int DefaultInternalAgentProxyConcurrency = 4;
    private readonly HashSet<string> internalAgentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IAsyncDisposable> internalAgentResources = [];
    private readonly object internalAgentLock = new();
    private SemaphoreSlim? internalAgentProxyGate;

    /// <summary>
    /// Creates a private, activation-scoped specialist with a separately tracked chat client,
    /// isolated tools, bounded execution, context compaction, and child attribution.
    /// </summary>
    protected async Task<InternalAgentResult> CreateInternalAgentAsync(
        InternalAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);

        if (options.Timeout <= TimeSpan.Zero || options.Timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "Internal-agent timeout must be finite and greater than zero.");
        if (options.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Internal-agent concurrency must be greater than zero.");
        if (options.ToolScope is not null && options.Tools is not null)
            throw new ArgumentException("Specify ToolScope or Tools, not both.", nameof(options));
        if (options.ToolScope is not null && options.ToolScope.ExecutionPolicy != options.ExecutionPolicy)
            throw new ArgumentException("The tool scope and internal agent must use the same execution policy.", nameof(options));

        lock (internalAgentLock)
        {
            if (!internalAgentNames.Add(options.Name.Trim()))
                throw new ArgumentException($"An internal agent named '{options.Name}' already exists in this proxy activation.", nameof(options));
        }

        try
        {
            var tools = options.ToolScope?.Tools ?? options.Tools?.ToArray() ?? [];
            var risks = options.ToolScope?.ToolRisks ?? options.ToolRisks;
            ValidateTools(tools, risks, options.ExecutionPolicy, requireExplicitRisk: tools.Count > 0);

            var chatClient = await GetChatClient(options.Model);
            var providers = new List<AIContextProvider>();
            if (options.EnableContextCompaction)
            {
                var compaction = await TryCreateContextCompactionProviderAsync(options.Model);
                if (compaction is not null) providers.Add(compaction);
            }

            var agentOptions = new ChatClientAgentOptions
            {
                Name = options.Name.Trim(),
                Description = options.Description.Trim(),
                ChatOptions = new ChatOptions
                {
                    Instructions = options.Instructions,
                    Tools = [.. tools]
                },
                AIContextProviders = providers.Count == 0 ? null : providers
            };

            var builder = new ChatClientAgent(chatClient, agentOptions).AsBuilder();
            if (options.EnableOpenTelemetry)
            {
                builder.UseOpenTelemetry(null, telemetry => telemetry.EnableSensitiveData = options.EnableSensitiveTelemetryData);
            }

            var inner = builder.Build(serviceProvider);
            var bounded = new BoundedInternalAgent(
                inner,
                fabrcoreAgentHost.GetHandle(),
                options.ExecutionPolicy,
                options.Timeout,
                options.MaxConcurrency,
                GetInternalAgentProxyGate(),
                serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System,
                serviceProvider.GetService<IAgentMessageMonitor>(),
                logger);

            lock (internalAgentLock)
            {
                internalAgentResources.Add(bounded);
            }

            logger.LogInformation(
                "Created internal agent {InternalAgent} for {Handle} with policy {Policy}, timeout {Timeout}, and {ToolCount} scoped tools",
                bounded.Name,
                fabrcoreAgentHost.GetHandle(),
                options.ExecutionPolicy,
                options.Timeout,
                tools.Count);

            return new InternalAgentResult(bounded, bounded.Name!, options.ExecutionPolicy, options.Timeout);
        }
        catch
        {
            lock (internalAgentLock)
            {
                internalAgentNames.Remove(options.Name.Trim());
            }
            throw;
        }
    }

    /// <summary>
    /// Resolves only the declared plugin, standalone-tool, and MCP capabilities for one specialist.
    /// Missing aliases, duplicate function names, and unsafe or unclassified tools fail closed.
    /// </summary>
    protected async Task<InternalAgentToolScope> ResolveInternalAgentToolsAsync(
        InternalAgentToolScopeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ScopeName);
        cancellationToken.ThrowIfCancellationRequested();

        var registry = serviceProvider.GetRequiredService<FabrCoreToolRegistry>();
        var resolved = await registry.ResolveToolScopeAsync(
            serviceProvider,
            options.Plugins,
            options.Tools,
            config,
            fabrcoreAgentHost,
            cancellationToken);

        var resources = new List<IAsyncDisposable> { resolved };
        var tools = new List<AITool>(resolved.Tools);
        try
        {
            foreach (var mcp in options.McpServers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mcpTools = await ConnectMcpServerAsync(mcp);
                if (mcpTools.Count == 0)
                    throw new InvalidOperationException($"MCP server '{mcp.Name ?? "(unnamed)"}' returned no tools for required scope '{options.ScopeName}'.");
                tools.AddRange(mcpTools);
            }

            var risks = new Dictionary<string, InternalAgentToolRisk>(options.ToolRisks, StringComparer.OrdinalIgnoreCase);
            ValidateTools(tools, risks, options.ExecutionPolicy, options.RequireExplicitRiskClassification);

            var scope = new InternalAgentToolScope(
                options.ScopeName.Trim(),
                tools,
                risks,
                options.ExecutionPolicy,
                resources);

            lock (internalAgentLock)
            {
                internalAgentResources.Add(scope);
            }

            logger.LogInformation(
                "Resolved internal-agent tool scope {Scope} for {Handle}: {ToolCount} tools, policy {Policy}",
                scope.Name,
                fabrcoreAgentHost.GetHandle(),
                tools.Count,
                options.ExecutionPolicy);

            return scope;
        }
        catch
        {
            await resolved.DisposeAsync();
            throw;
        }
    }

    private SemaphoreSlim GetInternalAgentProxyGate()
    {
        if (internalAgentProxyGate is not null) return internalAgentProxyGate;

        var maximum = DefaultInternalAgentProxyConcurrency;
        if (config.Args?.TryGetValue(InternalAgentArgs.MaxConcurrency, out var configured) is true
            && int.TryParse(configured, out var parsed)
            && parsed > 0)
        {
            maximum = Math.Min(parsed, 32);
        }

        lock (internalAgentLock)
        {
            return internalAgentProxyGate ??= new SemaphoreSlim(maximum, maximum);
        }
    }

    private static void ValidateTools(
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, InternalAgentToolRisk>? risks,
        InternalAgentExecutionPolicy policy,
        bool requireExplicitRisk)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var name = tool is AIFunction function && !string.IsNullOrWhiteSpace(function.Name)
                ? function.Name
                : tool.GetType().Name;

            if (!names.Add(name))
                throw new InvalidOperationException($"Internal-agent tool name '{name}' is duplicated.");

            var risk = GetRisk(risks, name);

            if (requireExplicitRisk && risk == InternalAgentToolRisk.Unclassified)
                throw new InvalidOperationException($"Internal-agent tool '{name}' has no explicit risk classification.");
            if (risk == InternalAgentToolRisk.SystemOnly)
                throw new InvalidOperationException($"System-only tool '{name}' cannot be exposed to an internal agent.");
            if (policy != InternalAgentExecutionPolicy.OrchestratorOnly
                && risk is not (InternalAgentToolRisk.Read or InternalAgentToolRisk.Compute))
            {
                throw new InvalidOperationException(
                    $"Background policy '{policy}' cannot expose '{name}' classified as '{risk}'. " +
                    "Mutation tools must remain on the approval-gated orchestration path.");
            }
        }
    }

    private static InternalAgentToolRisk GetRisk(
        IReadOnlyDictionary<string, InternalAgentToolRisk>? risks,
        string toolName)
    {
        if (risks is null) return InternalAgentToolRisk.Unclassified;
        if (risks.TryGetValue(toolName, out var exact)) return exact;

        foreach (var pair in risks)
        {
            if (string.Equals(pair.Key, toolName, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return InternalAgentToolRisk.Unclassified;
    }

    private async Task DisposeInternalAgentResourcesAsync()
    {
        List<IAsyncDisposable> resources;
        lock (internalAgentLock)
        {
            resources = [.. internalAgentResources];
            internalAgentResources.Clear();
            internalAgentNames.Clear();
        }

        foreach (var resource in resources.AsEnumerable().Reverse())
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose an internal-agent resource for {Handle}", fabrcoreAgentHost.GetHandle());
            }
        }

        internalAgentProxyGate?.Dispose();
        internalAgentProxyGate = null;
    }
}
