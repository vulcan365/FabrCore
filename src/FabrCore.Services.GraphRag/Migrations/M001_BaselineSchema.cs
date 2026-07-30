using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Baseline schema migration. Materializes the original 17 idempotent DDL
/// statements that previously lived inline in
/// <see cref="GraphRagSchemaInitializer.EnsureSchemaAsync"/>.
///
/// <para>
/// Every statement is <c>IF NOT EXISTS</c>-guarded, so this migration is a
/// safe no-op against any database that already has the v1 schema. That makes
/// it safe to apply to existing GraphRAG installations created before the
/// migration system existed.
/// </para>
/// </summary>
public sealed class M001_BaselineSchema : IGraphRagMigration
{
    public long Version => 1;
    public string Description => "Baseline GraphRAG schema (entities, relationships, chunks, scopes, hierarchy, source documents, contributions, ingestion metrics)";

    public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger)
    {
        var ddlStatements = new (string Name, string Ddl)[]
        {
            ("Canonical entity table",           GraphRagSchemaInitializer.GetCanonicalEntityDdl()),
            ("KnowledgeEntity node table",       GraphRagSchemaInitializer.GetKnowledgeEntityDdl()),
            ("KnowledgeRelationship edge table", GraphRagSchemaInitializer.GetKnowledgeRelationshipDdl()),
            ("KnowledgeChunk table",             GraphRagSchemaInitializer.GetKnowledgeChunkDdl()),
            ("Entity/chunk indexes",             GraphRagSchemaInitializer.GetIndexesDdl()),
            ("KnowledgeScope registry",          GraphRagSchemaInitializer.GetKnowledgeScopeDdl()),
            ("KnowledgeDomain node table",       GraphRagSchemaInitializer.GetKnowledgeDomainDdl()),
            ("KnowledgeCategory node table",     GraphRagSchemaInitializer.GetKnowledgeCategoryDdl()),
            ("BelongsTo edge table",             GraphRagSchemaInitializer.GetBelongsToDdl()),
            ("CommunitySummary table",           GraphRagSchemaInitializer.GetCommunitySummaryDdl()),
            ("Hierarchy indexes",                GraphRagSchemaInitializer.GetHierarchyIndexesDdl()),
            ("SourceDocument table",             GraphRagSchemaInitializer.GetSourceDocumentDdl()),
            ("SourceDocument indexes",           GraphRagSchemaInitializer.GetSourceDocumentIndexesDdl()),
            ("DocumentContribution table",       GraphRagSchemaInitializer.GetDocumentContributionDdl()),
            ("DocumentContribution indexes",     GraphRagSchemaInitializer.GetDocumentContributionIndexesDdl()),
            ("IngestionMetric table",            GraphRagSchemaInitializer.GetIngestionMetricDdl()),
            ("IngestionMetric indexes",          GraphRagSchemaInitializer.GetIngestionMetricIndexesDdl()),
        };

        foreach (var (name, ddl) in ddlStatements)
        {
            try
            {
                await using var command = new SqlCommand(ddl, connection, transaction);
                await command.ExecuteNonQueryAsync();
                logger.LogDebug("M001 baseline: {Name} ensured", name);
            }
            catch (SqlException ex)
            {
                logger.LogError(ex, "M001 baseline: failed to create {Name}", name);
                throw;
            }
        }
    }
}
