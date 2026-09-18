using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Scripting;

/// <summary>Inherit, assign a PluginAlias, and override Configure. Execution always uses a separate process.</summary>
public abstract class CSharpScriptingPluginBase : IFabrCorePlugin, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> executions = [];
    private ScriptingRuntime? runtime;
    private EnvironmentDefinition? definition;
    private PreparedEnvironment? prepared;
    private Task? initialization;
    private Task? disposal;
    private bool disposed;

    protected abstract void Configure(ScriptingEnvironmentBuilder environment);

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return initialization ??= InitializeCoreAsync(serviceProvider);
        }
    }

    private async Task InitializeCoreAsync(IServiceProvider services)
    {
        var builder = new ScriptingEnvironmentBuilder();
        Configure(builder);
        definition = builder.Build();
        runtime = services.GetRequiredService<ScriptingRuntime>();
        prepared = await runtime.PrepareAsync(definition, lifetime.Token);
    }

    [Description("Discover this C# scripting environment before writing code: packages, imports, usage guidance and limits. Scripts have Input (JsonElement), InputJson (string), and OutputDirectory (string).")]
    public ScriptingEnvironmentInfo GetScriptingEnvironment()
    {
        lock (gate)
        {
            EnsureInitialized();
            return new(prepared!.Id, definition!.Packages, Array.AsReadOnly(definition.Imports), definition.Instructions,
                definition.Timeout.TotalSeconds, definition.MemoryLimitBytes, definition.OutputLimit, definition.ArtifactLimit);
        }
    }

    [Description("Execute C# in a fresh separate worker using this plugin's configured packages. Call GetScriptingEnvironment first. Return a JSON-serializable value; use Console.WriteLine for bounded output or write files under OutputDirectory for returned artifacts. Variables and files do not persist between calls. #r and #load are disabled; package selection belongs to the developer.")]
    public async Task<ScriptExecutionResult> ExecuteCSharp(
        [Description("C# script source, optionally with a return statement.")] string code,
        [Description("Optional JSON input, available to the script as Input and InputJson.")] JsonElement? input = null,
        CancellationToken cancellationToken = default)
    {
        Task<ScriptExecutionResult> execution;
        CancellationTokenSource linked;
        lock (gate)
        {
            EnsureInitialized();
            linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            execution = runtime!.ExecuteAsync(prepared!, definition!, code, input, linked.Token);
            executions.Add(execution);
        }
        try { return await execution; }
        finally
        {
            linked.Dispose();
            lock (gate) executions.Remove(execution);
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (prepared == null) throw new InvalidOperationException("Initialize the scripting plugin before using its tools.");
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync();
        Task[] pending;
        lock (gate) pending = executions.Concat(initialization is null ? [] : new[] { initialization }).ToArray();
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Execution/initialization callers observe their own failures. */ }
        lifetime.Dispose();
    }
}
