using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Creates the GraphRAG schema and tables in SQL Server if they do not already exist.
/// Uses SQL Server 2025 / Azure SQL VECTOR data type and SQL Graph (node/edge) tables.
///
/// Scope model: every <see cref="SchemaName"/>.KnowledgeEntity row carries a single
/// required <c>ScopeKey</c> column. Chunks denormalize the same <c>ScopeKey</c> so
/// chunk search is a single-table filter. Relationships inherit from their endpoints
/// (enforced at write time — no cross-scope edges). Domains, categories, and
/// community summaries are shared taxonomy with no scope column.
/// </summary>
public static class GraphRagSchemaInitializer
{
    internal const string SchemaName = "grag";

    internal static string GetSchemaDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{SchemaName}')
        BEGIN
            EXEC('CREATE SCHEMA [{SchemaName}]');
        END
        """;

    internal static string GetKnowledgeEntityDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeEntity', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeEntity (
                    EntityId UNIQUEIDENTIFIER DEFAULT NEWID(),
                    CanonicalEntityId UNIQUEIDENTIFIER NOT NULL,
                    Name NVARCHAR(500) NOT NULL,
                    EntityType NVARCHAR(100) NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    Description NVARCHAR(MAX),
                    Content NVARCHAR(MAX),
                    Embedding VECTOR(1536),
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS NODE;
            ');
        END
        """;

    internal static string GetCanonicalEntityDdl() => $"""
        IF OBJECT_ID('{SchemaName}.CanonicalEntity', 'U') IS NULL
        BEGIN
            CREATE TABLE {SchemaName}.CanonicalEntity (
                CanonicalEntityId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                Name NVARCHAR(500) NOT NULL,
                EntityType NVARCHAR(100) NOT NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
        END
        """;

    internal static string GetKnowledgeRelationshipDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeRelationship', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeRelationship (
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

    internal static string GetKnowledgeChunkDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeChunk', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeChunk (
                    ChunkId UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
                    EntityId UNIQUEIDENTIFIER NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    Content NVARCHAR(MAX) NOT NULL,
                    Embedding VECTOR(1536),
                    ChunkIndex INT NOT NULL,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    internal static string GetIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeEntity_Name_Type_Scope' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeEntity'))
            CREATE UNIQUE INDEX IX_KnowledgeEntity_Name_Type_Scope ON {SchemaName}.KnowledgeEntity (Name, EntityType, ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeEntity_ScopeKey' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeEntity'))
            CREATE INDEX IX_KnowledgeEntity_ScopeKey ON {SchemaName}.KnowledgeEntity (ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeEntity_Canonical_Scope' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeEntity'))
            CREATE INDEX IX_KnowledgeEntity_Canonical_Scope ON {SchemaName}.KnowledgeEntity (CanonicalEntityId, ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CanonicalEntity_Name_Type' AND object_id = OBJECT_ID('{SchemaName}.CanonicalEntity'))
            CREATE UNIQUE INDEX UX_CanonicalEntity_Name_Type ON {SchemaName}.CanonicalEntity (Name, EntityType);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeRelationship_ScopeKey' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeRelationship'))
            CREATE INDEX IX_KnowledgeRelationship_ScopeKey ON {SchemaName}.KnowledgeRelationship (ScopeKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeChunk_EntityId_Index' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeChunk'))
            CREATE INDEX IX_KnowledgeChunk_EntityId_Index ON {SchemaName}.KnowledgeChunk (EntityId, ChunkIndex);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeChunk_Scope_Entity' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeChunk'))
            CREATE INDEX IX_KnowledgeChunk_Scope_Entity ON {SchemaName}.KnowledgeChunk (ScopeKey, EntityId);
        """;

    // ─── Scope Registry ──────────────────────────────────────────────────

    internal static string GetKnowledgeScopeDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeScope', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeScope (
                    ScopeKey NVARCHAR(200) NOT NULL PRIMARY KEY,
                    Description NVARCHAR(MAX),
                    DefaultPriority FLOAT DEFAULT 1.0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    // ─── Hierarchy Tables ────────────────────────────────────────────────

    internal static string GetKnowledgeDomainDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeDomain', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeDomain (
                    DomainId UNIQUEIDENTIFIER DEFAULT NEWID(),
                    Name NVARCHAR(200) NOT NULL,
                    Description NVARCHAR(MAX),
                    PriorityWeight FLOAT DEFAULT 1.0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS NODE;
            ');
        END
        """;

    internal static string GetKnowledgeCategoryDdl() => $"""
        IF OBJECT_ID('{SchemaName}.KnowledgeCategory', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.KnowledgeCategory (
                    CategoryId UNIQUEIDENTIFIER DEFAULT NEWID(),
                    Name NVARCHAR(300) NOT NULL,
                    Description NVARCHAR(MAX),
                    Embedding VECTOR(1536),
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS NODE;
            ');
        END
        """;

    internal static string GetBelongsToDdl() => $"""
        IF OBJECT_ID('{SchemaName}.BelongsTo', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.BelongsTo (
                    ScopeKey NVARCHAR(200) NULL,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                ) AS EDGE;
            ');
        END
        """;

    internal static string GetCommunitySummaryDdl() => $"""
        IF OBJECT_ID('{SchemaName}.CommunitySummary', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.CommunitySummary (
                    SummaryId UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
                    CategoryId UNIQUEIDENTIFIER NOT NULL,
                    ScopeKey NVARCHAR(200) NULL,
                    Summary NVARCHAR(MAX) NOT NULL,
                    Embedding VECTOR(1536),
                    EntityCount INT DEFAULT 0,
                    Metadata NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    // ─── Source Document Table ────────────────────────────────────────

    internal static string GetSourceDocumentDdl() => $"""
        IF OBJECT_ID('{SchemaName}.SourceDocument', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.SourceDocument (
                    DocumentId UNIQUEIDENTIFIER DEFAULT NEWID() PRIMARY KEY,
                    FileName NVARCHAR(500) NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    SourceKind NVARCHAR(50) NOT NULL DEFAULT ''Markdown'',
                    SourceKey NVARCHAR(500) NOT NULL,
                    SourceTitle NVARCHAR(500) NULL,
                    SourceOccurredAtUtc DATETIME2(7) NULL,
                    MetadataJson NVARCHAR(MAX) NULL,
                    MarkdownContent NVARCHAR(MAX) NOT NULL,
                    FileSizeBytes BIGINT NOT NULL,
                    EntityId UNIQUEIDENTIFIER NULL,
                    ChunkCount INT DEFAULT 0,
                    ExtractedEntityCount INT DEFAULT 0,
                    ExtractedRelationshipCount INT DEFAULT 0,
                    ContentHash CHAR(64) NULL,
                    InstructionHash CHAR(64) NULL,
                    VersionNumber INT NOT NULL DEFAULT 1,
                    LockedAt DATETIME2(3) NULL,
                    LockedBy NVARCHAR(128) NULL,
                    Status NVARCHAR(50) DEFAULT ''Pending'',
                    ErrorMessage NVARCHAR(MAX),
                    CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
                );
            ');
        END
        """;

    internal static string GetSourceDocumentIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SourceDocument_Scope_Source' AND object_id = OBJECT_ID('{SchemaName}.SourceDocument'))
            CREATE UNIQUE INDEX UX_SourceDocument_Scope_Source ON {SchemaName}.SourceDocument (ScopeKey, SourceKind, SourceKey);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SourceDocument_Scope_FileName' AND object_id = OBJECT_ID('{SchemaName}.SourceDocument'))
            CREATE INDEX IX_SourceDocument_Scope_FileName ON {SchemaName}.SourceDocument (ScopeKey, FileName);
        """;

    // ─── Document Contribution (provenance / reference counting) ─────────

    internal static string GetDocumentContributionDdl() => $"""
        IF OBJECT_ID('{SchemaName}.DocumentContribution', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.DocumentContribution (
                    ContributionId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    DocumentId UNIQUEIDENTIFIER NOT NULL,
                    ItemKind TINYINT NOT NULL,
                    EntityId UNIQUEIDENTIFIER NULL,
                    RelFromEntityId UNIQUEIDENTIFIER NULL,
                    RelToEntityId UNIQUEIDENTIFIER NULL,
                    RelationshipType NVARCHAR(100) NULL,
                    DomainId UNIQUEIDENTIFIER NULL,
                    CategoryId UNIQUEIDENTIFIER NULL,
                    BelongsToShape TINYINT NULL,
                    VersionNumber INT NOT NULL,
                    CreatedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_DC_Document FOREIGN KEY (DocumentId)
                        REFERENCES {SchemaName}.SourceDocument(DocumentId) ON DELETE CASCADE
                );
            ');
        END
        """;

    internal static string GetDocumentContributionIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_Entity' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_Entity ON {SchemaName}.DocumentContribution (EntityId)
                WHERE EntityId IS NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_Rel' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_Rel ON {SchemaName}.DocumentContribution (RelFromEntityId, RelToEntityId, RelationshipType)
                WHERE ItemKind IN (2, 6);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_Domain' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_Domain ON {SchemaName}.DocumentContribution (DomainId)
                WHERE DomainId IS NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_Cat' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_Cat ON {SchemaName}.DocumentContribution (CategoryId)
                WHERE CategoryId IS NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_BelongsTo' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_BelongsTo ON {SchemaName}.DocumentContribution (RelFromEntityId, CategoryId, DomainId, BelongsToShape)
                WHERE ItemKind = 5;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DC_Doc' AND object_id = OBJECT_ID('{SchemaName}.DocumentContribution'))
            CREATE INDEX IX_DC_Doc ON {SchemaName}.DocumentContribution (DocumentId, VersionNumber);
        """;

    // ─── Ingestion Metric (per-run chat-token telemetry) ─────────────────

    internal static string GetIngestionMetricDdl() => $"""
        IF OBJECT_ID('{SchemaName}.IngestionMetric', 'U') IS NULL
        BEGIN
            EXEC('
                CREATE TABLE {SchemaName}.IngestionMetric (
                    MetricId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    DocumentId UNIQUEIDENTIFIER NOT NULL,
                    VersionNumber INT NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    ChatModelName NVARCHAR(200) NULL,
                    ChatInputTokens BIGINT NOT NULL DEFAULT 0,
                    ChatOutputTokens BIGINT NOT NULL DEFAULT 0,
                    ChatCallCount INT NOT NULL DEFAULT 0,
                    ChatTotalMs BIGINT NOT NULL DEFAULT 0,
                    DurationMs BIGINT NOT NULL,
                    CreatedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_IM_Document FOREIGN KEY (DocumentId)
                        REFERENCES {SchemaName}.SourceDocument(DocumentId) ON DELETE CASCADE
                );
            ');
        END
        """;

    internal static string GetIngestionMetricIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IM_Document' AND object_id = OBJECT_ID('{SchemaName}.IngestionMetric'))
            CREATE INDEX IX_IM_Document ON {SchemaName}.IngestionMetric (DocumentId, VersionNumber);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IM_CreatedAt' AND object_id = OBJECT_ID('{SchemaName}.IngestionMetric'))
            CREATE INDEX IX_IM_CreatedAt ON {SchemaName}.IngestionMetric (CreatedAt);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IM_Scope' AND object_id = OBJECT_ID('{SchemaName}.IngestionMetric'))
            CREATE INDEX IX_IM_Scope ON {SchemaName}.IngestionMetric (ScopeKey, CreatedAt);
        """;

    internal static string GetHierarchyIndexesDdl() => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeDomain_Name' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeDomain'))
            CREATE UNIQUE INDEX IX_KnowledgeDomain_Name ON {SchemaName}.KnowledgeDomain (Name);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KnowledgeCategory_Name' AND object_id = OBJECT_ID('{SchemaName}.KnowledgeCategory'))
            CREATE UNIQUE INDEX IX_KnowledgeCategory_Name ON {SchemaName}.KnowledgeCategory (Name);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommunitySummary_CategoryId' AND object_id = OBJECT_ID('{SchemaName}.CommunitySummary'))
            CREATE INDEX IX_CommunitySummary_CategoryId ON {SchemaName}.CommunitySummary (CategoryId);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommunitySummary_Category_Scope' AND object_id = OBJECT_ID('{SchemaName}.CommunitySummary'))
            CREATE UNIQUE INDEX UX_CommunitySummary_Category_Scope ON {SchemaName}.CommunitySummary (CategoryId, ScopeKey);
        """;

    /// <summary>
    /// Ensures the GraphRAG schema is at the latest version by running every
    /// pending entry from
    /// <see cref="Migrations.GraphRagMigrationRunner.RunMigrationsAsync"/>.
    ///
    /// <para>
    /// Public entry point preserved for backward compatibility — the
    /// <c>GraphRagSchemaHostedService</c> and any external callers continue to
    /// invoke this method. The actual DDL now lives inside individual
    /// <see cref="Migrations.IGraphRagMigration"/> classes; the original
    /// "create everything" body is <see cref="Migrations.M001_BaselineSchema"/>.
    /// </para>
    /// </summary>
    public static async Task EnsureSchemaAsync(string connectionString, ILogger? logger = null)
    {
        await Migrations.GraphRagMigrationRunner.RunMigrationsAsync(connectionString, logger);
    }
}
