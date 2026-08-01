using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Creates the agent memory tables in the mem schema if they do not already exist.
/// Uses SQL Server 2025 / Azure SQL VECTOR data type and SQL Graph (NODE/EDGE) tables.
///
/// Table roles:
///   MemoryEntity (NODE) — concept nodes: what the agent knows about
///   MemoryChunk          — content + embeddings: the actual knowledge (1+ chunks per entity)
///   MemoryRelationship (EDGE) — typed, weighted, directed edges between entities
///   MemorySummaryNode    — hierarchical topic rollups built during consolidation
///   MemoryScope          — scope registry (shared pools + auto-registered agent scopes)
///   MemoryAuditLog       — who/what/when trail of memory-changing actions
///
/// The embedding dimension is fixed at schema creation time (VECTOR columns cannot be
/// altered) — changing <c>AgentMemoryOptions.EmbeddingDimensions</c> afterwards requires
/// dropping the mem schema.
/// </summary>
internal static class MemorySchemaInitializer
{
    internal const string SchemaName = "mem";

    internal static string GetSchemaDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{SchemaName}')
            EXEC('CREATE SCHEMA [{SchemaName}]');
        """;

    /// <summary>
    /// MemoryEntity — concept node. Stores metadata about what the agent knows.
    /// Content and embeddings live in MemoryChunk (not on this table).
    /// Exception: the __MEMORY_INDEX__ sentinel row stores hot index JSON in the Content column.
    /// </summary>
    internal static string GetMemoryEntityDdl() => $"""
        IF OBJECT_ID('{SchemaName}.MemoryEntity', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemoryEntity (
                    EntityId UNIQUEIDENTIFIER DEFAULT NEWID(),
                    ScopeKey NVARCHAR(200) NOT NULL,
                    Name NVARCHAR(500) NOT NULL,
                    EntityType NVARCHAR(100) NOT NULL,
                    Description NVARCHAR(MAX),
                    Content NVARCHAR(MAX),
                    Visibility NVARCHAR(20) NOT NULL DEFAULT ''Warm'',
                    IsPointInTime BIT NOT NULL DEFAULT 0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS NODE;
            ');
        END
        """;

    /// <summary>
    /// MemoryRelationship — directed, typed, weighted edge between two MemoryEntity nodes.
    /// Represents how concepts relate (e.g., Job 1 → has_plate → Plate 11001).
    /// </summary>
    internal static string GetMemoryRelationshipDdl() => $"""
        IF OBJECT_ID('{SchemaName}.MemoryRelationship', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemoryRelationship (
                    ScopeKey NVARCHAR(200) NOT NULL,
                    RelationshipType NVARCHAR(200) NOT NULL,
                    Description NVARCHAR(MAX),
                    Weight FLOAT DEFAULT 1.0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS EDGE;
            ');
        END
        """;

    /// <summary>
    /// MemoryChunk — primary content + embedding store. Each entity has at least one chunk.
    /// Vector search targets this table, JOINed to MemoryEntity for metadata context.
    /// </summary>
    internal static string GetMemoryChunkDdl(int embeddingDimensions) => $"""
        IF OBJECT_ID('{SchemaName}.MemoryChunk', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemoryChunk (
                    ChunkId UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    EntityId UNIQUEIDENTIFIER NOT NULL,
                    Content NVARCHAR(MAX) NOT NULL,
                    Embedding VECTOR({embeddingDimensions}),
                    ChunkIndex INT NOT NULL DEFAULT 0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    /// <summary>
    /// MemorySummaryNode — hierarchical semantic rollup. Each row is a topic-level NL summary of
    /// a set of underlying memories (or, at deeper levels, child summary nodes). Populated by
    /// <c>IMemorySummaryTree</c> during consolidation. Separate from MemoryEntity so a rebuild can
    /// truncate and rebuild without touching raw memories.
    /// </summary>
    internal static string GetMemorySummaryNodeDdl(int embeddingDimensions) => $"""
        IF OBJECT_ID('{SchemaName}.MemorySummaryNode', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemorySummaryNode (
                    NodeId UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    ParentNodeId UNIQUEIDENTIFIER NULL,
                    Depth INT NOT NULL DEFAULT 0,
                    Topic NVARCHAR(500) NOT NULL,
                    Summary NVARCHAR(MAX) NOT NULL,
                    Embedding VECTOR({embeddingDimensions}),
                    MemberCount INT NOT NULL DEFAULT 0,
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    /// <summary>
    /// MemoryScope — scope registry. Shared pools are created explicitly; agent-handle
    /// scopes are auto-registered on first write so admin tooling can enumerate them.
    /// </summary>
    internal static string GetMemoryScopeDdl() => $"""
        IF OBJECT_ID('{SchemaName}.MemoryScope', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemoryScope (
                    ScopeKey NVARCHAR(200) NOT NULL PRIMARY KEY,
                    Description NVARCHAR(MAX) NULL,
                    IsShared BIT NOT NULL DEFAULT 0,
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    CreatedBy NVARCHAR(200) NULL
                );
            ');
        END
        """;

    /// <summary>
    /// MemoryAuditLog — best-effort trail of memory-changing actions (agent saves,
    /// admin edits, consolidations, scope lifecycle).
    /// </summary>
    internal static string GetMemoryAuditLogDdl() => $"""
        IF OBJECT_ID('{SchemaName}.MemoryAuditLog', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.MemoryAuditLog (
                    AuditId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    OccurredAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                    ActionType NVARCHAR(80) NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    MemoryId UNIQUEIDENTIFIER NULL,
                    ActorId NVARCHAR(200) NULL,
                    ActorName NVARCHAR(200) NULL,
                    Summary NVARCHAR(500) NULL,
                    Payload NVARCHAR(MAX) NULL,
                    DurationMs BIGINT NULL
                );
            ');
        END
        """;

    internal static string GetIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryEntity_Scope_Name_Type' AND object_id = OBJECT_ID('{SchemaName}.MemoryEntity'))
            CREATE UNIQUE INDEX IX_MemoryEntity_Scope_Name_Type ON {SchemaName}.MemoryEntity (ScopeKey, Name, EntityType);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryEntity_ScopeKey' AND object_id = OBJECT_ID('{SchemaName}.MemoryEntity'))
            CREATE INDEX IX_MemoryEntity_ScopeKey ON {SchemaName}.MemoryEntity (ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryChunk_EntityId_Index' AND object_id = OBJECT_ID('{SchemaName}.MemoryChunk'))
            CREATE INDEX IX_MemoryChunk_EntityId_Index ON {SchemaName}.MemoryChunk (EntityId, ChunkIndex);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryChunk_ScopeKey' AND object_id = OBJECT_ID('{SchemaName}.MemoryChunk'))
            CREATE INDEX IX_MemoryChunk_ScopeKey ON {SchemaName}.MemoryChunk (ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryChunk_ScopeKey_EntityId' AND object_id = OBJECT_ID('{SchemaName}.MemoryChunk'))
            CREATE INDEX IX_MemoryChunk_ScopeKey_EntityId ON {SchemaName}.MemoryChunk (ScopeKey, EntityId);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemorySummaryNode_ScopeKey' AND object_id = OBJECT_ID('{SchemaName}.MemorySummaryNode'))
            CREATE INDEX IX_MemorySummaryNode_ScopeKey ON {SchemaName}.MemorySummaryNode (ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemorySummaryNode_Parent' AND object_id = OBJECT_ID('{SchemaName}.MemorySummaryNode'))
            CREATE INDEX IX_MemorySummaryNode_Parent ON {SchemaName}.MemorySummaryNode (ScopeKey, ParentNodeId);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MemoryAuditLog_Scope_Time' AND object_id = OBJECT_ID('{SchemaName}.MemoryAuditLog'))
            CREATE INDEX IX_MemoryAuditLog_Scope_Time ON {SchemaName}.MemoryAuditLog (ScopeKey, OccurredAt DESC);
        """;

    public static async Task EnsureSchemaAsync(string connectionString, int embeddingDimensions, ILogger? logger = null)
    {
        if (embeddingDimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(embeddingDimensions),
                "EmbeddingDimensions must be a positive integer.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Multiple silos or application instances can start against the same empty
        // database concurrently. Serialize the existence-check/create sequence so two
        // initializers cannot both observe a missing index or table and race to create it.
        const string lockResource = "FabrCore.Services.Memory.SchemaInitialization";
        await using (var lockCommand = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 60000;
            IF @result < 0
                THROW 51000, 'Timed out acquiring the FabrCore memory schema initialization lock.', 1;
            """, connection))
        {
            lockCommand.Parameters.AddWithValue("@resource", lockResource);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var ddlStatements = new[]
        {
            ("Schema", GetSchemaDdl()),
            ("MemoryEntity node table", GetMemoryEntityDdl()),
            ("MemoryRelationship edge table", GetMemoryRelationshipDdl()),
            ("MemoryChunk table", GetMemoryChunkDdl(embeddingDimensions)),
            ("MemorySummaryNode table", GetMemorySummaryNodeDdl(embeddingDimensions)),
            ("MemoryScope table", GetMemoryScopeDdl()),
            ("MemoryAuditLog table", GetMemoryAuditLogDdl()),
            ("Indexes", GetIndexesDdl())
        };

        try
        {
            foreach (var (name, ddl) in ddlStatements)
            {
                try
                {
                    await using var command = new SqlCommand(ddl, connection);
                    await command.ExecuteNonQueryAsync();
                    logger?.LogDebug("Memory schema: {Name} ensured", name);
                }
                catch (SqlException ex)
                {
                    logger?.LogError(ex, "Memory schema: failed to create {Name}", name);
                    throw;
                }
            }
        }
        finally
        {
            await using var releaseCommand = new SqlCommand(
                "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';",
                connection);
            releaseCommand.Parameters.AddWithValue("@resource", lockResource);
            await releaseCommand.ExecuteNonQueryAsync();
        }

        logger?.LogInformation("Memory schema initialization complete (schema: {Schema}, vector dims: {Dims})",
            SchemaName, embeddingDimensions);
    }
}
