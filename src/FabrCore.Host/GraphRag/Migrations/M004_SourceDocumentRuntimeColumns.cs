using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Ensures existing <c>grag.SourceDocument</c> tables have the runtime columns
/// required by document listing, re-ingest short-circuiting, and ingest locks.
/// Fresh databases already receive these columns from the baseline DDL, but
/// pre-migration databases can have the table without this later metadata.
/// </summary>
public sealed class M004_SourceDocumentRuntimeColumns : IGraphRagMigration
{
    public long Version => 4;
    public string Description => "Add SourceDocument content hash, version, and ingest lock columns";

    public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;

        var columnsDdl = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'ContentHash') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD ContentHash CHAR(64) NULL;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'VersionNumber') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD VersionNumber INT NOT NULL
                            CONSTRAINT DF_SourceDocument_VersionNumber DEFAULT 1;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'LockedAt') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD LockedAt DATETIME2(3) NULL;');

                IF COL_LENGTH(N'{{schema}}.SourceDocument', N'LockedBy') IS NULL
                    EXEC(N'ALTER TABLE {{schema}}.SourceDocument
                        ADD LockedBy NVARCHAR(128) NULL;');
            END
            """;

        await using var command = new SqlCommand(columnsDdl, connection, transaction);
        await command.ExecuteNonQueryAsync();
        logger.LogDebug("M004: SourceDocument runtime columns ensured");
    }
}
