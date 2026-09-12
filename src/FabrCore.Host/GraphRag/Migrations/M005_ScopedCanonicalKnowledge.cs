using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Separates globally reusable identity and taxonomy from scope-owned evidence.
/// Existing entities receive a canonical identity by name and type. Graph
/// relationships and entity-to-taxonomy assignments receive the source
/// entity's scope; category-to-domain taxonomy edges remain global.
/// </summary>
public sealed class M005_ScopedCanonicalKnowledge : IGraphRagMigration
{
    public long Version => 5;
    public string Description => "Add canonical identities and scope graph evidence and taxonomy assignments";

    public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;
        var columnsDdl = $$"""
            IF OBJECT_ID(N'{{schema}}.CanonicalEntity', N'U') IS NULL
            BEGIN
                EXEC(N'
                    CREATE TABLE {{schema}}.CanonicalEntity (
                        CanonicalEntityId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                        Name NVARCHAR(500) NOT NULL,
                        EntityType NVARCHAR(100) NOT NULL,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );');
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CanonicalEntity_Name_Type' AND object_id = OBJECT_ID(N'{{schema}}.CanonicalEntity'))
                EXEC(N'CREATE UNIQUE INDEX UX_CanonicalEntity_Name_Type
                    ON {{schema}}.CanonicalEntity (Name, EntityType);');

            IF COL_LENGTH(N'{{schema}}.KnowledgeEntity', N'CanonicalEntityId') IS NULL
                EXEC(N'ALTER TABLE {{schema}}.KnowledgeEntity ADD CanonicalEntityId UNIQUEIDENTIFIER NULL;');

            IF COL_LENGTH(N'{{schema}}.KnowledgeRelationship', N'ScopeKey') IS NULL
                EXEC(N'ALTER TABLE {{schema}}.KnowledgeRelationship ADD ScopeKey NVARCHAR(200) NULL;');

            IF COL_LENGTH(N'{{schema}}.BelongsTo', N'ScopeKey') IS NULL
                EXEC(N'ALTER TABLE {{schema}}.BelongsTo ADD ScopeKey NVARCHAR(200) NULL;');

            IF COL_LENGTH(N'{{schema}}.CommunitySummary', N'ScopeKey') IS NULL
                EXEC(N'ALTER TABLE {{schema}}.CommunitySummary ADD ScopeKey NVARCHAR(200) NULL;');
            """;

        // Run DML in a separate command so SQL Server compiles it only after
        // the columns above exist. A single batch fails upgrades with error 207
        // because SQL Server binds column references before executing ALTER TABLE.
        var backfillDml = $$"""
            INSERT INTO {{schema}}.CanonicalEntity (CanonicalEntityId, Name, EntityType)
            SELECT NEWID(), source.Name, source.EntityType
            FROM (SELECT DISTINCT Name, EntityType FROM {{schema}}.KnowledgeEntity) source
            WHERE NOT EXISTS (
                SELECT 1 FROM {{schema}}.CanonicalEntity canonical
                WHERE canonical.Name = source.Name AND canonical.EntityType = source.EntityType);

            UPDATE entity
            SET CanonicalEntityId = canonical.CanonicalEntityId
            FROM {{schema}}.KnowledgeEntity entity
            INNER JOIN {{schema}}.CanonicalEntity canonical
                ON canonical.Name = entity.Name AND canonical.EntityType = entity.EntityType
            WHERE entity.CanonicalEntityId IS NULL;

            UPDATE relationship
            SET ScopeKey = sourceEntity.ScopeKey
            FROM {{schema}}.KnowledgeRelationship relationship
            INNER JOIN {{schema}}.KnowledgeEntity sourceEntity
                ON relationship.$from_id = sourceEntity.$node_id
            WHERE relationship.ScopeKey IS NULL;

            UPDATE assignment
            SET ScopeKey = entity.ScopeKey
            FROM {{schema}}.BelongsTo assignment
            INNER JOIN {{schema}}.KnowledgeEntity entity
                ON assignment.$from_id = entity.$node_id
            WHERE assignment.ScopeKey IS NULL;

            ;WITH duplicateSummaries AS (
                SELECT SummaryId,
                       ROW_NUMBER() OVER (
                           PARTITION BY CategoryId, ScopeKey
                           ORDER BY UpdatedAt DESC, CreatedAt DESC, SummaryId) AS RowNumber
                FROM {{schema}}.CommunitySummary
            )
            DELETE FROM duplicateSummaries WHERE RowNumber > 1;
            """;

        var constraintsDdl = $$"""
            IF EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'{{schema}}.KnowledgeEntity')
                  AND name = N'CanonicalEntityId' AND is_nullable = 1)
                EXEC(N'ALTER TABLE {{schema}}.KnowledgeEntity ALTER COLUMN CanonicalEntityId UNIQUEIDENTIFIER NOT NULL;');

            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE name = N'FK_KnowledgeEntity_CanonicalEntity'
                  AND parent_object_id = OBJECT_ID(N'{{schema}}.KnowledgeEntity'))
                EXEC(N'ALTER TABLE {{schema}}.KnowledgeEntity WITH CHECK
                    ADD CONSTRAINT FK_KnowledgeEntity_CanonicalEntity
                    FOREIGN KEY (CanonicalEntityId) REFERENCES {{schema}}.CanonicalEntity(CanonicalEntityId);');

            IF EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'{{schema}}.KnowledgeRelationship')
                  AND name = N'ScopeKey' AND is_nullable = 1)
                EXEC(N'ALTER TABLE {{schema}}.KnowledgeRelationship ALTER COLUMN ScopeKey NVARCHAR(200) NOT NULL;');
            """;

        var indexesDdl = $$"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KnowledgeEntity_Canonical_Scope' AND object_id = OBJECT_ID(N'{{schema}}.KnowledgeEntity'))
                CREATE INDEX IX_KnowledgeEntity_Canonical_Scope
                    ON {{schema}}.KnowledgeEntity (CanonicalEntityId, ScopeKey);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KnowledgeRelationship_ScopeKey' AND object_id = OBJECT_ID(N'{{schema}}.KnowledgeRelationship'))
                CREATE INDEX IX_KnowledgeRelationship_ScopeKey
                    ON {{schema}}.KnowledgeRelationship (ScopeKey);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BelongsTo_ScopeKey' AND object_id = OBJECT_ID(N'{{schema}}.BelongsTo'))
                CREATE INDEX IX_BelongsTo_ScopeKey ON {{schema}}.BelongsTo (ScopeKey);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CommunitySummary_Category_Scope' AND object_id = OBJECT_ID(N'{{schema}}.CommunitySummary'))
                CREATE UNIQUE INDEX UX_CommunitySummary_Category_Scope
                    ON {{schema}}.CommunitySummary (CategoryId, ScopeKey);
            """;

        var scopeBackfillDml = $$"""
            INSERT INTO {{schema}}.KnowledgeScope (ScopeKey, Description)
            SELECT discovered.ScopeKey, N'Backfilled from existing GraphRAG data'
            FROM (
                SELECT ScopeKey FROM {{schema}}.KnowledgeEntity
                UNION SELECT ScopeKey FROM {{schema}}.KnowledgeChunk
                UNION SELECT ScopeKey FROM {{schema}}.SourceDocument
                UNION SELECT ScopeKey FROM {{schema}}.KnowledgeRelationship
            ) discovered
            WHERE discovered.ScopeKey IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM {{schema}}.KnowledgeScope scope
                  WHERE scope.ScopeKey = discovered.ScopeKey);
            """;

        await ExecuteAsync(columnsDdl, connection, transaction);
        await ExecuteAsync(backfillDml, connection, transaction);
        await ExecuteAsync(constraintsDdl, connection, transaction);
        await ExecuteAsync(indexesDdl, connection, transaction);
        await ExecuteAsync(scopeBackfillDml, connection, transaction);
        logger.LogDebug("M005: canonical identities and scoped graph evidence ensured");
    }

    private static async Task ExecuteAsync(string sql, SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
