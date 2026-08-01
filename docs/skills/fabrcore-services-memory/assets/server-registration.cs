// FabrCore server Program.cs — memory service registration example

using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Register FabrCore server (provides IEmbeddings, IFabrCoreChatClientService)
builder.Services.AddFabrCoreServer(options =>
{
    // ... your FabrCore server configuration
});

// 2. Register agent memory services (auto-creates the mem schema and tables on startup).
//    The connection string name is required; startup fails fast when it is missing,
//    schema creation fails, or IEmbeddings is not registered.
builder.Services.AddAgentMemoryServices("MemoryDb", options =>
{
    // Optional: embedding vector dimension — must match your embeddings model.
    // Fixed at schema creation; changing it later requires dropping the mem schema.
    options.EmbeddingDimensions = 1536;    // Default: 1536

    // Optional: customize hot layer caps
    options.HotIndex.MaxEntries = 20;      // Max index entries (default: 20)
    options.HotIndex.MaxTokens = 3000;     // Max estimated tokens (default: 3000)

    // Optional: customize retrieval behavior
    options.Retrieval.WarmRetrievalLimit = 5;     // Max warm memories per query (default: 5)
    options.Retrieval.HeaderScanLimit = 200;      // Max headers scanned per query (default: 200)
    options.Retrieval.FreshnessDaysThreshold = 1; // Days before staleness warning (default: 1)

    // Optional: customize consolidation
    options.Consolidation.MemoryFileCap = 200;            // Max entities per scope (default: 200)
    options.Consolidation.EnableAutoConsolidation = false; // Auto-compact on save (default: false)
    options.Consolidation.DuplicateDistanceThreshold = 0.05; // Cosine distance for dedup (default: 0.05)

    // Optional: mark all extracted memories as point-in-time snapshots.
    // Use for data query agents where facts are database results that go stale immediately.
    options.PointInTimeMemories = false;   // Default: false

    // Optional: customize LLM models for retrieval, compaction, and imagining
    options.Models.RelevanceModelName = "default"; // Must match fabrcore.json entry
    options.Models.CompactionModelName = "default";
    options.Models.ImaginingModelName = "default"; // LLM for synthetic imagining query generation

    // Optional: customize synthetic imagining
    options.Retrieval.MaxImaginingQueries = 5;     // Max queries per ImagineAsync call (default: 5)

    // Optional: restrict allowed memory types
    options.AllowedMemoryTypes = new HashSet<MemoryType>
    {
        MemoryType.Fact,
        MemoryType.Rule,
        MemoryType.Instruction,
        MemoryType.Observation,
        MemoryType.Procedural
    };
});

// 3. Optional: administration surface for admin UIs (e.g. the FabrCore.Surface.Admin
//    memory page at /surface/admin/memory) and maintenance tooling.
builder.Services.AddMemoryAdministration();

var app = builder.Build();
app.Run();

// ─── Shared memory scopes ────────────────────────────────────────────
// By default each agent's memory is isolated under its own handle. To give a
// fleet of agents one shared memory pool (e.g. every bank-reconciliation agent
// learns "Habitat line items are business meal expenses" when one is taught),
// configure the plugin setting on each agent:
//
//   "plugins": ["agent-memory"],
//   "args": { "agent-memory:MemoryScope": "bank-recon" }
//
// Service-driven agents can do the same in code:
//
//   var scope = MemoryScopeResolver.Resolve(config);            // honors MemoryScope settings
//   var memory = memoryProvider.GetMemoryService(scope);        // shared instance per scope
//
// Shared scopes can be pre-created (with a description) via IMemoryScopeService
// or from the Surface admin memory page.
