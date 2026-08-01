using FabrCore.Services.GraphRag;

// Register core GraphRAG services and startup schema initialization.
builder.Services.AddGraphRagServices(
    connectionStringName: "GraphRagDb",
    extractionModelName: "graph-extraction");

// Register only when building admin screens, maintenance workflows, or APIs
// over the administration service surface.
builder.Services.AddGraphRagAdministration();
