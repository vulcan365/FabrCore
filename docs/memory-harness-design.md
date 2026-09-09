# Memory for long-running FabrCore agents

Reviewed September 7, 2026. Microsoft Agent Framework source was read from
`C:/repos/microsoft/agent-framework` at commit `8e803b97a241e208fea9a78eeb6bf54a83fcef11`.
No files in that repository were changed. Implementation builds against FabrCore's existing
package references; this work does not upgrade the framework.

## Decisions

Keep `FabrCore.Services.Memory` as the durable memory backend and keep both code and tool
interfaces. They are two entry points into the same service, taxonomy and SQL scopes.
Code handles lifecycle actions the host must reliably perform; tools support agent-selected
save, correction and retrieval. A scoped Agent Framework context provider supplies bounded
recall to the FabrCore harness. Avoid adopting a second memory database or automatically
enabling Microsoft's default filesystem memory in the shared silo.

Long-running continuity has three separate responsibilities:

1. **Working state:** current intent, todos, approvals, job identifiers and pending work.
   FabrCore's durable harness/session state and history summaries own this.
2. **Reusable knowledge:** preferences, rules, facts, observations and procedures.
   SQL memory owns this, with bounded context assembly and on-demand retrieval.
3. **Execution recovery:** timeouts, checkpoints, retries, resumable jobs and idempotent
   external actions. The runtime and application own this. Memory alone cannot make an
   agent run forever or resume an external action exactly once.

Compaction should save reusable knowledge before replacing history while preserving the
active task in the handover summary. Do not assume that extraction captured everything.
Do not convert every progress update or recalled memory into another durable fact.

## Industry/source review

| Source | Relevant approach | FabrCore decision |
| --- | --- | --- |
| [LangChain memory](https://docs.langchain.com/oss/python/concepts/memory) | Thread context versus long-term namespaced memory; semantic, episodic and procedural knowledge; writes in application flow or background | Keep shared scoped storage and both code/tool entry points. Do not collapse working state into durable facts. |
| [Anthropic context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) | Compaction, structured notes and specialist agents address long horizons together | Preserve actionable summaries alongside durable memory; bounded recall is part of context management. |
| [Letta shared blocks](https://docs.letta.com/tutorials/attaching-detaching-blocks/) | Independently stored blocks can be attached to several agents | Support developer-selected core/own composition without duplicating shared facts into every subagent. |
| [Mem0 paper](https://arxiv.org/abs/2504.19413) | Extraction and consolidation of salient information, with an optional graph variant | Retain existing extraction/consolidation hooks. Do not adopt new graph complexity or claim vendor benchmark results without local evidence. |
| [LongMemEval](https://github.com/xiaowu0162/LongMemEval) | Extraction, multi-session reasoning, temporal reasoning, knowledge updates and abstention | Use these dimensions in regression design. The small FabrCore corpus is not the published benchmark. |
| [Microsoft harness](https://learn.microsoft.com/en-us/agent-framework/agents/harness) and local source | Composable context providers, persistent todos, memory tools and per-call history persistence | Integrate through the existing provider seam; assess recovery semantics separately from memory quality. |

The local Microsoft source is particularly useful here:

- `Microsoft.Agents.AI/Harness/FileMemory/FileMemoryProvider.cs` exposes read/write/replace,
  list and search tools over **AgentFileStore**, which is pluggable rather than inherently
  local-disk-only. It injects a pointer index as a user message. Its default harness backing
  store is filesystem-based and its default working folder is generated per session.
- `Microsoft.Agents.AI/Memory/ChatHistoryMemoryProvider.cs` supports automatic recall and
  on-demand function calling. This is direct evidence that both access styles fit the framework.
- `Microsoft.Agents.AI.Harness/HarnessAgent.cs` explicitly assembles per-service-call history
  persistence and in-loop compaction. FabrCore assembles its own providers and snapshots
  harness state after runs. Those are different recovery boundaries. A separate fault-injection
  comparison is needed before claiming equivalent mid-turn crash recovery.

Deferred: background “sleep” consolidation as a durable job, full event-time/supersession
modeling, raw episodic archives, and a file-compatible `AgentFileStore` adapter. They may be
valuable, but should be separate measured changes rather than new defaults bundled together.

## Harness integration

The integration lives in the optional Memory package; the SDK does not depend on SQL Memory.
`WithMemory` places memory recall before the harness's existing compaction providers, so
the supplied memory participates in their context budgeting.
Automatic recall is invocation-scoped, not an extra retrieval call before every tool-loop
model request. The default memory context cap is 12,000 characters, an explicit character
budget rather than an assertion about the model's exact token count.

```csharp
var memory = serviceProvider.GetRequiredService<IAgentMemoryProvider>()
    .GetMemoryService(MemoryScopeResolver.Resolve(config));

var harness = await CreateFabrCoreHarnessAgent(
    config.Models ?? "default", threadId: "main",
    configure: options => options.WithMemoryLifecycle(memory, serviceProvider, includeTools: true));
```

Use either `includeTools: true` or configured plugin tools, not both: duplicate names should
not be exposed. Automatic recall is optional; callers can instead construct the plugin or
use `MemoryHarnessExtensions.CreateMemoryTools(memory)` for a tool-only experience.
The context provider also works directly in `ChatClientAgentOptions.AIContextProviders`.

`WithMemoryLifecycle` also registers `MemoryCompactionHandler` on this harness's history
registration. The default proxy `OnCompaction` dispatches to that callback; a custom override
must call the base implementation or explicitly use the handler. `WithMemoryCompaction(handler)`
is available when constructing the handler yourself. `WithMemory` alone provides recall/tools.
Standalone harness construction has no proxy history lifecycle; its host must invoke compaction.
This is history compaction; the cheap per-call projection/compaction layer remains separate.
Initialization-time memory injection is insufficient for a long-lived agent because it
does not refresh after corrections or other agents' writes.

## Internal agents

Private specialists do not have their own FabrCore grain handles. The developer supplies
a stable name and an authorized core scope; changing the name changes the private pool.

| Mode | Reads | Writes/extraction/consolidation |
| --- | --- | --- |
| `CoreOnly` | Core | Core |
| `CoreAndOwn` | Own, then core, within shared result/index caps | Own |
| `OwnOnly` (default) | Own | Own |

Own scope keys use length-prefixed parent/name components to avoid ambiguous concatenation.
These remain data partitions, not tenant authorization. Do not expose arbitrary scope strings
as model-controlled tool arguments. CoreAndOwn deliberately does not let a child edit or
forget a core entry; promotion can be performed explicitly by the core agent or trusted code.

```csharp
var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
var childMemory = provider.ForInternalAgent(
    MemoryScopeResolver.Resolve(config), "research",
    InternalAgentMemoryMode.CoreAndOwn,
    serviceProvider.GetRequiredService<AgentMemoryOptions>());
var memoryTools = MemoryHarnessExtensions.CreateMemoryTools(childMemory);

var child = await CreateInternalAgentAsync(new InternalAgentOptions {
    Name = "research",
    Description = "Researches and retains scoped observations.",
    Instructions = "Treat recalled notes as evidence, verify uncertain claims, and preserve provenance.",
    Model = config.Models ?? "default",
    Tools = memoryTools,
    ToolRisks = MemoryHarnessExtensions.GetMemoryToolRisks(memoryTools),
    AIContextProviders = [new AgentMemoryContextProvider(childMemory)],
    ExecutionPolicy = InternalAgentExecutionPolicy.ConcurrentWithMemory
});
// child.AsBackgroundAgent() can be supplied to the harness's BackgroundAgents.
```

`ConcurrentWithMemory` is opt-in. The existing `ConcurrentReadOnly` and `SerializedReadOnly`
policies still reject writes. Memory writes require both the explicit `MemoryWrite`
classification and a tool implementing `IScopedAgentMemoryTool` with a fixed write scope.
The Memory factory supplies that binding. External mutation/administration tools remain
prohibited in the background. The interface is a trusted host contract, not a sandbox against
arbitrary code running in the same process. Context providers likewise must not inject
undeclared tools or side effects.

For read-only specialists use `CreateMemoryTools(childMemory, readOnly: true)` and the
existing read-only execution policy. Code callbacks can still save observations through the
same service when the host chooses to do so.

## Reliability changes and remaining limits

- Empty relevance selections now remain empty, including small pools. Valid IDs are constrained
  by the response schema and results are deduplicated/capped. Model outages still use the
  legacy recency fallback; that is degraded retrieval, not proof of relevance.
- Omitted descriptions receive bounded content previews, so header selection can see dates,
  identifiers and procedure details. Explicit content corrections refresh generated previews.
- Compaction extracts the final selected prefix even when forced to shrink the keep window.
  It excludes injected memory/system messages, propagates cancellation, and preserves history
  on extraction errors, missing/empty summaries or summaries that would expand history.
- Invalid extraction payloads and failed extracted-memory saves propagate rather than looking
  like successful empty extraction, including through the public compaction handler.
  SQL facade extraction now commits the batch and its receipt in one transaction.
  Tool-output compression still does not archive raw tool results;
  explicitly save relevant tool findings or retain their original source.
- The provider bounds injected context; direct service results and tool responses are not a
  universal token-budget guarantee. Large corpora need broader candidate retrieval and budget
  work beyond the current header cap. Own-first layered selection can use the budget before
  core results; measure this before introducing a global reranker.
- SQL facade save/update/forget operations commit entity, chunk and index changes together.
  Scope application locks serialize these operations across store instances, including extraction,
  with a 15-second lock wait and two-minute operation deadline. Last serialized explicit correction
  wins; this prevents interleaved partial writes, not semantic conflicts between competing agents.
  Model work can hold the lock, so contention and timeout rates need sustained-load measurement.
- Extraction receipts fingerprint the ordered user/assistant source text and author names within
  a scope. Identical transcripts reuse the committed IDs without calling the extraction model.
  Changed transcripts are new batches; ordinary save calls have no caller-supplied idempotency key.
  A receipt replay loads current entries and does not recreate deleted ones. Receipts retain a hash
  and IDs, not source text; scope deletion removes them. Configuration changes alone do not reprocess
  a completed source. Custom `IMemoryStore` implementations retain their own transaction semantics.
- Facade knowledge writes invalidate that scope's derived summary tree in the same transaction.
  Consolidation rebuilds it later. Facade consolidation and admin scope deletion share the mutation
  lock; consolidation itself remains a sequence of maintenance operations rather than an atomic batch.
  Direct low-level store/compactor callers must provide their own orchestration. Audit entries are
  best-effort after commit and are not a durable transactional outbox.
- Storage quotas, receipt retention policies, durable scheduled consolidation, and process-crash
  recovery validation remain follow-up work. No indefinite-execution guarantee is implied.

See the [evaluation console](../src/FabrCore.Services.Memory.EvalConsole/README.md) and
[benchmark checkpoint](memory-eval-status.md) for reproducible runs and measured limits.
