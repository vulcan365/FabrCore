---
name: fabrcore-scripting
description: "Add FabrCore 2.0 C# scripting tools with per-plugin NuGet packages, out-of-process Roslyn execution, JSON results and artifacts. Use for CSharpScriptingPluginBase, ScriptingEnvironmentBuilder, agent selection and worker deployment."
metadata:
  author: FabrCore
  version: 2.0.0
  documentation: https://fabrcore.ai/docs/scripting
---

# FabrCore C# scripting

Use the optional `FabrCore.Scripting` package for calculations and transformations
that an agent writes at runtime. Keep database authorization, connection ownership
and business operations in application tools; pass bounded, authorized query results
into scripts. Scripting does not automatically replace a query engine.

## Implement the capability

1. Reference `FabrCore.Scripting` and call `services.AddFabrCoreScripting()` once
   in the application. Prepare with the .NET 10 SDK; execute with the .NET 10 runtime.
   Use a matching locally built package set while stable 2.0 publication is pending.
2. Derive a concrete `[PluginAlias]` class from `CSharpScriptingPluginBase`. Override
   only `Configure(ScriptingEnvironmentBuilder)`. Start from
   [ReportingScriptsPlugin.cs](assets/ReportingScriptsPlugin.cs).
3. Declare exact NuGet versions, imports, instructions and limits in that method.
   The developer chooses packages; the model cannot install them with `#r` or `#load`.
   Package dependencies are restored into a separate worker, not the agent host.
4. Include the concrete plugin's assembly in normal plugin discovery. Add its alias
   to `config.Plugins` before `ResolveConfiguredToolsAsync()` in the proxy.
   [ScriptingAgent.cs](assets/ScriptingAgent.cs) is a complete proxy example.
5. Select one scripting plugin per agent because inherited tool names collide.
   Many agents may select the same plugin. Different plugins can declare different
   package versions. Matching dependency caches are shared; execution state is not.
6. Have the model inspect `GetScriptingEnvironment` before calling `ExecuteCSharp`.
   Supply useful package APIs/examples in `WithInstructions`; a package name is not
   enough to document an application helper library.

## Inputs, returns and lifetime

The script sees `Input` (JsonElement), `InputJson` (string) and `OutputDirectory`.
Every invocation starts a fresh process and temporary directory. Live agents, DI
services, sessions and object references do not cross this boundary.

Use `return new { ... };` for structured data and `Console.WriteLine` for diagnostics.
The worker serializes the returned object with System.Text.Json. The host reads
that JSON into `ScriptExecutionResult.Value`, then the plugin returns the result
through the normal AIFunction tool call to the model. Inspect `Status` before using
`Value`; compiler diagnostics and runtime errors are results the model can correct.

Files written under `OutputDirectory` become bounded artifact names and byte arrays.
The application must store/present those bytes if it needs durable downloads or a
Blazor view. Cleanup removes execution files. This feature does not compile Razor
or load generated Blazor components; prefer validated data rendered by known components.

The base plugin owns initialization and cancellation/disposal. Normal proxy tool
resolution retains its owned plugin scope until teardown. Direct registry callers
should use and dispose `ResolveToolScopeAsync`; raw `ResolveToolsAsync` does not
manage an owned lifetime.

## Execution boundary

Process separation is not a security sandbox. The worker uses the host OS identity;
files, network, child processes and external effects remain subject to OS permissions.
Memory checks sample the worker working set; they are not hard OS or process-tree
quotas. Timeouts do not undo side effects. Untrusted workloads need an external
restricted OS/container execution boundary. Do not invent permission-builder APIs.

## Supporting material

- [Runtime reference](references/runtime.md): complete setup, options, cache preparation,
  private feeds, output limits, failure behavior and deployment boundaries.
- [Program.cs](assets/Program.cs) and [ScriptingExample.csproj](assets/ScriptingExample.csproj):
  a no-model registry/tool checkpoint. Copy the whole assets folder and run
  `dotnet run --project ScriptingExample.csproj`. It returns a total of 400 and
  different host/worker process IDs. Supply your matching available package version
  with `-p:FabrCoreVersion=...` when needed.
- [order-total.csx](assets/order-total.csx) and [input.json](assets/input.json):
  reusable tool payloads; pass the script as `code` and parsed JSON as `input`.

For host deployment/prewarming, read the runtime reference before choosing where
to store the prepared cache. Rebuild it explicitly when updating dependency graphs.
