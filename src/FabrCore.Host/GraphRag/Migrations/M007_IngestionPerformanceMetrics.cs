using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Adds durable phase-level telemetry used to diagnose and regress ingestion latency.
/// </summary>
public sealed class M007_IngestionPerformanceMetrics : IGraphRagMigration
{
    public long Version => 7;
    public string Description => "Add phase-level ingestion performance metrics";

    public async Task ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;
        var ddl = $$"""
            IF OBJECT_ID(N'{{schema}}.IngestionMetric', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ResolvedModelName') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ResolvedModelName NVARCHAR(200) NULL;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ChunkEmbeddingMs') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ChunkEmbeddingMs BIGINT NOT NULL CONSTRAINT DF_IM_ChunkEmbeddingMs DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'DocumentEmbeddingMs') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD DocumentEmbeddingMs BIGINT NOT NULL CONSTRAINT DF_IM_DocumentEmbeddingMs DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'LlmExtractionMs') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD LlmExtractionMs BIGINT NOT NULL CONSTRAINT DF_IM_LlmExtractionMs DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'EntityEmbeddingMs') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD EntityEmbeddingMs BIGINT NOT NULL CONSTRAINT DF_IM_EntityEmbeddingMs DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'SqlWriteMs') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD SqlWriteMs BIGINT NOT NULL CONSTRAINT DF_IM_SqlWriteMs DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'EmbeddingBatchCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD EmbeddingBatchCount INT NOT NULL CONSTRAINT DF_IM_EmbeddingBatchCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'SqlCommandBatchCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD SqlCommandBatchCount INT NOT NULL CONSTRAINT DF_IM_SqlCommandBatchCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ChunkCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ChunkCount INT NOT NULL CONSTRAINT DF_IM_ChunkCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ExtractedEntityCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ExtractedEntityCount INT NOT NULL CONSTRAINT DF_IM_ExtractedEntityCount DEFAULT 0;
                IF COL_LENGTH(N'{{schema}}.IngestionMetric', N'ExtractedRelationshipCount') IS NULL
                    ALTER TABLE {{schema}}.IngestionMetric ADD ExtractedRelationshipCount INT NOT NULL CONSTRAINT DF_IM_ExtractedRelationshipCount DEFAULT 0;
            END
            """;

        await using var command = new SqlCommand(ddl, connection, transaction);
        await command.ExecuteNonQueryAsync();
        logger.LogDebug("M007: ingestion performance metric columns ensured");
    }
}
