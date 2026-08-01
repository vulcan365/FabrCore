using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Adds first-class source metadata to <c>grag.SourceDocument</c> so ingestion
/// can identify non-file sources, such as emails, by stable source ids.
/// </summary>
public sealed class M003_SourceDocumentMetadata : IGraphRagMigration
{
    public long Version => 3;
    public string Description => "Add source metadata columns and source-key identity for ingested documents";

    public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;

        var columnsDdl = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKind') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD SourceKind NVARCHAR(50) NOT NULL
                            CONSTRAINT DF_SourceDocument_SourceKind DEFAULT ''Markdown'';');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKey') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD SourceKey NVARCHAR(500) NULL;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceTitle') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD SourceTitle NVARCHAR(500) NULL;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceOccurredAtUtc') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD SourceOccurredAtUtc DATETIME2(7) NULL;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'MetadataJson') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD MetadataJson NVARCHAR(MAX) NULL;');
            END
            """;

        var backfillDml = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
               AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKind') IS NOT NULL
               AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKey') IS NOT NULL
               AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceTitle') IS NOT NULL
            BEGIN
                EXEC(N'
                    UPDATE {{schema}}.SourceDocument
                    SET SourceKind = ISNULL(NULLIF(SourceKind, ''''), ''Markdown''),
                        SourceKey = ISNULL(NULLIF(SourceKey, ''''), FileName),
                        SourceTitle = ISNULL(NULLIF(SourceTitle, ''''), FileName)
                    WHERE SourceKind IS NULL
                       OR SourceKind = ''''
                       OR SourceKey IS NULL
                       OR SourceKey = ''''
                       OR SourceTitle IS NULL
                       OR SourceTitle = '''';');
            END
            """;

        var constraintsDdl = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
               AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKind') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c
                        ON c.object_id = dc.parent_object_id
                       AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'{{schema}}.SourceDocument')
                      AND c.name = N'SourceKind'
               )
            BEGIN
                EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                    ADD CONSTRAINT DF_SourceDocument_SourceKind DEFAULT ''Markdown'' FOR SourceKind;');
            END

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'{{schema}}.SourceDocument')
                  AND name = N'SourceKind'
                  AND is_nullable = 1
            )
                EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                    ALTER COLUMN SourceKind NVARCHAR(50) NOT NULL;');

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'{{schema}}.SourceDocument')
                  AND name = N'SourceKey'
                  AND is_nullable = 1
            )
                EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                    ALTER COLUMN SourceKey NVARCHAR(500) NOT NULL;');
            """;

        var indexesDdl = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SourceDocument_Scope_FileName' AND object_id = OBJECT_ID(N'{{schema}}.SourceDocument'))
                    EXEC(N'DROP INDEX UX_SourceDocument_Scope_FileName ON {{schema}}.SourceDocument;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'ScopeKey') IS NOT NULL
                   AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKind') IS NOT NULL
                   AND COL_LENGTH(N'{{schema}}.SourceDocument', N'SourceKey') IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SourceDocument_Scope_Source' AND object_id = OBJECT_ID(N'{{schema}}.SourceDocument'))
                    EXEC(N'CREATE UNIQUE INDEX UX_SourceDocument_Scope_Source ON {{schema}}.SourceDocument (ScopeKey, SourceKind, SourceKey);');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'ScopeKey') IS NOT NULL
                   AND COL_LENGTH(N'{{schema}}.SourceDocument', N'FileName') IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SourceDocument_Scope_FileName' AND object_id = OBJECT_ID(N'{{schema}}.SourceDocument'))
                    EXEC(N'CREATE INDEX IX_SourceDocument_Scope_FileName ON {{schema}}.SourceDocument (ScopeKey, FileName);');
            END
            """;

        await ExecuteAsync(columnsDdl, connection, transaction);
        logger.LogDebug("M003: SourceDocument metadata columns ensured");

        await ExecuteAsync(backfillDml, connection, transaction);
        logger.LogDebug("M003: SourceDocument metadata backfilled");

        await ExecuteAsync(constraintsDdl, connection, transaction);
        logger.LogDebug("M003: SourceDocument metadata constraints ensured");

        await ExecuteAsync(indexesDdl, connection, transaction);
        logger.LogDebug("M003: SourceDocument source identity indexes ensured");
    }

    private static async Task ExecuteAsync(string sql, SqlConnection connection, SqlTransaction transaction)
    {
        await using var cmd = new SqlCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }
}
