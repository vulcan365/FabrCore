using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Adds a non-reversible hash of caller extraction guidance so instruction
/// changes participate in ingestion idempotency without storing the guidance.
/// </summary>
public sealed class M006_SourceDocumentInstructionHash : IGraphRagMigration
{
    public long Version => 6;
    public string Description => "Add SourceDocument extraction instruction hash";

    public async Task ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;
        var ddl = $$"""
            IF OBJECT_ID(N'{{schema}}.SourceDocument', N'U') IS NOT NULL
               AND COL_LENGTH(N'{{schema}}.SourceDocument', N'InstructionHash') IS NULL
                ALTER TABLE {{schema}}.SourceDocument ADD InstructionHash CHAR(64) NULL;
            """;

        await using var command = new SqlCommand(ddl, connection, transaction);
        await command.ExecuteNonQueryAsync();
        logger.LogDebug("M006: SourceDocument instruction hash ensured");
    }
}
