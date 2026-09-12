using FabrCore.Host;

// FabrCore 2.0: configure ConnectionStrings:FabrCore through secrets and supply
// default chat + 1536-dimensional embeddings models in the active model store.
// Memory tuning binds from FabrCore:Memory; see appsettings.memory.json.
// For split storage set FabrCore:Database:MemoryConnectionStringName.
var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer();
var app = builder.Build();
app.UseFabrCoreServer();
app.Run();

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
