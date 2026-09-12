using FabrCore.Host;

// Supply ConnectionStrings:FabrCore via secrets and configure default chat plus
// 1536-dimensional embeddings. This one call registers SQL GraphRAG and admin services.
// Optional extraction alias: FabrCore:GraphRag:ExtractionModelName.
// Optional split database: FabrCore:Database:GraphRagConnectionStringName.
builder.AddFabrCoreServer();
