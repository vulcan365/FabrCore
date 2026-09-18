# C# scripting plugins

`FabrCore.Scripting` is an optional package. Developers define scripting environments in C# by deriving from `CSharpScriptingPluginBase`. Agents enable these plugins through the existing plugin registry. Each invocation runs in a fresh .NET process, using Roslyn and the packages declared by the plugin.

**Process separation is not a security sandbox.** The worker runs as the host OS user. It can access that user's files and network, and installed packages can execute arbitrary code. Do not enable the local worker for mutually untrusted users without an external OS/container isolation boundary. Neither package references nor disabled `#r`/`#load` directives enforce permissions. The worker does not receive the host's environment secrets or live service instances, but that does not prevent access to resources the OS user can read.

## Register the runtime

Reference `FabrCore.Scripting` from the application/agent library. Configure the runtime once in the application's services:

```csharp
using FabrCore.Scripting;

builder.Services.AddFabrCoreScripting(options =>
{
    options.CacheDirectory = Path.Combine(builder.Environment.ContentRootPath, "scripting-cache");
    options.WorkingDirectory = Path.Combine(Path.GetTempPath(), "my-app-scripts");
    // Optional: explicit NuGet configuration, including private package feeds.
    // options.NuGetConfigPath = "/deployment/NuGet.Config";
});
```

The first preparation requires the .NET 10 SDK and access to the packages through NuGet. Framework-dependent execution requires .NET 10 and the same OS/architecture for which the environment was prepared. The compiler is pinned to Roslyn 5.0.0. The agent host does not load Roslyn or the script's selected packages.

## Define a reusable plugin

```csharp
using FabrCore.Scripting;
using FabrCore.Sdk;

[PluginAlias("reporting-scripts")]
public sealed class ReportingScriptsPlugin : CSharpScriptingPluginBase
{
    protected override void Configure(ScriptingEnvironmentBuilder environment)
    {
        environment
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .AddPackage("MathNet.Numerics", "5.0.0")
            .AddImports("Newtonsoft.Json", "Newtonsoft.Json.Linq", "MathNet.Numerics")
            .WithTimeout(TimeSpan.FromSeconds(30))
            .WithMemoryLimit(512L * 1024 * 1024)
            .WithOutputLimit(32_768)
            .WithArtifactLimit(1_048_576)
            .WithInstructions("Use C# for calculations and transforming query results. " +
                "Verify that query results are complete before calculating totals.");
    }
}
```

Select it in the agent before resolving its tools:

```csharp
public override async Task OnInitialize()
{
    if (!config.Plugins.Contains("reporting-scripts"))
        config.Plugins.Add("reporting-scripts");

    var tools = await ResolveConfiguredToolsAsync();
    var result = await CreateChatClientAgent(
        chatClientConfigName: config.Models ?? "default",
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
        tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}
```

Include the assembly containing `ReportingScriptsPlugin` in the application's configured plugin discovery assemblies. The registry discovers the inherited `ExecuteCSharp` and `GetScriptingEnvironment` tools. Protected configuration hooks are not tools. The base class owns initialization and disposal; derived classes only override `Configure`.

Select one scripting plugin per agent: their inherited tool names are the same. Different agents can select the same plugin, or different plugins with different package sets. Dependencies can be shared without sharing execution state. Conflicting versions belong in different plugins/environments, not in one package graph.

`AddPackage` accepts an exact version (including an exact prerelease). NuGet restores transitive dependencies and records the resolved graph in `packages.lock.json`. Floating versions and version ranges are rejected. Roslyn packages are reserved to the worker. Applications can supply their own helper/API-client NuGet packages, including packages from private feeds. Those packages must work in a .NET 10 console application on the target platform. Packages requiring extra shared frameworks, custom service initialization or a web host are not automatically configured. This is not a Razor component compiler.

Package configuration is developer code and is trusted. NuGet package build targets execute during environment preparation. Feed configuration and credentials should be supplied through normal NuGet deployment mechanisms, not script input.

## Script inputs and results

`GetScriptingEnvironment` describes the declared packages, imports, developer guidance and limits. The tool description tells the model to inspect this before writing code. Package names alone are not API documentation: provide useful signatures/examples through `WithInstructions` or the agent's normal knowledge/tools.

`ExecuteCSharp(string code, JsonElement? input, CancellationToken cancellationToken)` accepts structured JSON input. A script can use:

| Global | Meaning |
| --- | --- |
| `Input` | `System.Text.Json.JsonElement`; JSON null when no input is supplied |
| `InputJson` | The JSON input as text |
| `OutputDirectory` | This execution's directory for output artifacts |

Example script with the reporting plugin:

```csharp
var data = JObject.Parse(InputJson);
var total = data["orders"]!.Values<decimal>().Sum();
Console.WriteLine("Calculated order total.");
return new { Customer = data["customer"]!.ToString(), Total = total };
```

Input:

```json
{"customer":"Eric","orders":[125,75,200]}
```

`ScriptExecutionResult` contains a status, JSON value, captured console output, error text and output artifacts. Compilation errors include compiler diagnostics for correction. Ordinary exceptions become `RuntimeError`; unexpected exits become `WorkerError`. Timeout, cancellation, memory and output limits have distinct statuses.

Create files under `OutputDirectory` to return `ScriptArtifact` entries (relative name and bytes). The host/application is responsible for storing these bytes and presenting download links. There are at most 100 artifacts and a combined byte limit. Reparse points/symlinks are skipped. All execution files are removed after collection; the next invocation starts with no variables or files from the previous call.

Do not return an enormous dataset through the tool: return a summary or bounded artifact. The JSON result, console output and error text are bounded separately. Return-value serialization uses a conservative UTF-8 byte bound equal to `MaxOutputCharacters`, so non-ASCII values may hit it earlier. Artifact contents are binary bytes and may be expensive to include in a model's context; applications can intercept/store them before presentation.

## Preparation and deployment

The environment is keyed by exact requested package versions, worker source/protocol, target framework and OS/architecture. Imports, instructions and execution limits remain per plugin; changing them does not rebuild identical dependencies. Preparation uses a filesystem lock across processes and publishes atomically. Failed builds are not marked ready. Successful caches retain source, the dependency lock file and published worker assets, including package native assets selected for the target runtime.

You can prewarm before serving agents:

```csharp
var runtime = app.Services.GetRequiredService<ScriptingRuntime>();
await runtime.PreparePluginAsync<ReportingScriptsPlugin>(app.Services);
```

To avoid SDK/package access in production, run this step on the target platform during deployment, preserve the resulting cache directory, and configure `AllowEnvironmentPreparation = false` on the production host. The worker still needs the .NET 10 runtime. Preparation fails clearly if an expected environment is missing. Private NuGet configuration is not copied into execution directories. Keep the prepared cache private to the application identity; do not share it across mutually untrusted applications or package sources. Delete/reprepare a cache entry explicitly to refresh its transitive dependency graph.

Configuration executed in `OnInitialize` is not discovered automatically by MSBuild. Package preparation is explicit at deployment or occurs on first plugin initialization. The runtime does not install packages chosen by the model.

## Limits and lifecycle

- Default deadline: 30 seconds, including worker startup and compilation. The supervisor attempts to terminate the worker process tree when cancelled or timed out.
- Default memory limit: 512 MiB of worker working set, sampled every 50 ms. This is a best-effort supervisor limit, not a hard OS quota and not a total across child processes.
- Default limits: 32,768 characters of console output, a bounded JSON value, 1 MiB of artifacts, 128 Ki characters of code, 1 Mi characters of JSON input.
- A separate execution directory is used for every call. The package cache contains no agent state. Workers are not pooled.
- Disposal cancels and waits for a plugin's active invocations. Normal proxy tool resolution now retains disposable plugins until proxy teardown. Explicit `ResolveToolScopeAsync` callers must dispose their returned scope.
- Tool calls use the registry's existing execution-evidence wrapper. Runtime logs identify the environment, outcome and duration without logging source or input data.

Generated code can still start other processes, access files outside its working directory, modify shared caches, use the network or perform external side effects if its OS identity permits it. A process that deliberately escapes supervision is not contained by this implementation. A timeout does not roll back external effects. Use a restricted execution host for untrusted workloads; no in-process filtering is presented as a security guarantee.

## Validation

The [SampleApp scripting demo](https://github.com/vulcan365/FabrCore/blob/main/samples/FabrCore.SampleApp/README.md#try-c-scripting) exposes a
sales-reporting agent in Surface. Its test exercises the blueprint-selected plugin through the
tool registry with real C# execution and verifies calculations and the returned JSON artifact,
without making a model call:

```powershell
dotnet test samples/FabrCore.SampleApp.Tests/FabrCore.SampleApp.Tests.csproj --configuration Release --filter FullyQualifiedName~ScriptingDemoTests
```

Run the [console sample](../assets/Program.cs) without a model/API key:

```powershell
dotnet run --project samples/FabrCore.Scripting.Sample/FabrCore.Scripting.Sample.csproj
```

It selects the reporting plugin through the actual registry, runs Newtonsoft.Json in a worker, and reports a total of 400 with separate host/worker process IDs.

```powershell
dotnet test src/FabrCore.Scripting.Tests/FabrCore.Scripting.Tests.csproj
dotnet test src/FabrCore.Sdk.Tests/FabrCore.Sdk.Tests.csproj --filter FullyQualifiedName~ConfiguredPluginLifetimeTests
```

Worker tests are marked `Integration`: they require the .NET 10 SDK and NuGet access/cache, and exercise real processes. The normal offline repository filter excludes them. They cover separate process execution, per-plugin packages/versions, inherited tool discovery, data/artifact handling, fresh state, timeout, cancellation, output limits and abrupt exits.
