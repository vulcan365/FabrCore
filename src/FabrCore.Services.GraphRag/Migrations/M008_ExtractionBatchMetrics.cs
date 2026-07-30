using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Adds model identity and concurrent extraction batch diagnostics.
/// </summary>
public sealed class M008_ExtractionBatchMetrics : IGraphRagMigration
{
    public long Version => 8;
    public string Description => "Add extraction batch and model diagnostics";

    public async Task ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;
        var ddl = $$"""
            IF OBJECT_ID(N'{{schema}}.IngestionMetric', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ResolvedProviderName') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ResolvedProviderName NVARCHAR(100) NULL;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ResolvedDeploymentModelName') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ResolvedDeploymentModelName NVARCHAR(200) NULL;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ExtractionBatchCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ExtractionBatchCount INT NOT NULL CONSTRAINT DF_IM_ExtractionBatchCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ExtractionRetryCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ExtractionRetryCount INT NOT NULL CONSTRAINT DF_IM_ExtractionRetryCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ExtractionTruncationCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ExtractionTruncationCount INT NOT NULL CONSTRAINT DF_IM_ExtractionTruncationCount DEFAULT 0;
            END
            """;

        await using var command = new SqlCommand(ddl, connection, transaction);
        await command.ExecuteNonQueryAsync();
        logger.LogDebug("M008: extraction batch metric columns ensured");
    }
}
