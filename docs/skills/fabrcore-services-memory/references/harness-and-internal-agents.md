# Harness and internal-agent memory

Use the extension methods in FabrCore.Services.Memory.Configuration on the options passed
to the proxy-created FabrCore harness. See [example](../assets/memory-harness.cs).

- WithMemory(memory, includeTools: false, maxContextCharacters: 12000) prepends bounded
  recall to the existing Microsoft Agent Framework context-provider pipeline.
- WithMemoryCompaction(handler) installs memory-aware persisted-history compaction.
- WithMemoryLifecycle(memory, services, includeTools: false, maxContextCharacters: 12000)
  configures both. It resolves compaction dependencies from registered services.
- Tools are opt-in; existing tools/providers are preserved. Register memory only once.
  The compaction callback is set by these helpers, so compose any application-specific
  callback deliberately rather than expecting multiple callbacks to run automatically.

The cap applies to injected recall context, not storage, the entire model prompt, or all
tool output. Explicit service saves work independently of recall or compaction.

## Private specialists

Resolve a stable parent scope and specialist name in trusted host code:

| Mode | Reads | Writes |
| --- | --- | --- |
| OwnOnly (default) | Specialist scope | Specialist scope |
| CoreOnly | Core scope | Core scope |
| CoreAndOwn | Own first, then core | Specialist scope only |

Combined recall merges the two sources; it does not copy core memories into own storage.
Core-only IDs cannot be updated or forgotten through the own-write facade.
The derived own scope must fit the 200-character SQL scope limit.

CreateMemoryTools(memory, readOnly: true) returns three read tools suitable for ordinary
ConcurrentReadOnly specialists. For intentional background memory writes, use the complete
scoped tool set, GetMemoryToolRisks(tools), and ConcurrentWithMemory explicitly. That policy
does not authorize unrelated external mutations. Keep timeouts and concurrency bounded.

Pass the configured InternalAgentOptions to the proxy's internal-agent creation API and
register its returned AsBackgroundAgent() with harness BackgroundAgents when appropriate.
Creating a memory facade alone does not start a specialist or schedule work.
