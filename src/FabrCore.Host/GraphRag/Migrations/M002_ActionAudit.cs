using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Adds the <c>grag.ActionAudit</c> table and its supporting indexes. This
/// table captures user/admin actions (searches, scope creates, document
/// deletes, etc.). Token-level ingestion telemetry continues to live in the
/// dedicated <c>grag.IngestionMetric</c> table — they're separate concerns.
/// </summary>
public sealed class M002_ActionAudit : IGraphRagMigration
{
    public long Version => 2;
    public string Description => "Add grag.ActionAudit and its indexes for user/admin action logging";

    public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger)
    {
        var schema = GraphRagSchemaInitializer.SchemaName;

        var tableDdl = $$"""
            IF OBJECT_ID('{{schema}}.ActionAudit', 'U') IS NULL
            BEGIN
                CREATE TABLE {{schema}}.ActionAudit (
                    AuditId        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    OccurredAt     DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),

                    ActionType     NVARCHAR(80)  NOT NULL,
                    Severity       TINYINT       NOT NULL DEFAULT 0,

                    ActorKind      NVARCHAR(20)  NULL,
                    ActorId        NVARCHAR(200) NULL,
                    ActorName      NVARCHAR(200) NULL,

                    SubjectKind    NVARCHAR(40)  NULL,
                    SubjectId      NVARCHAR(200) NULL,

                    ScopeKey       NVARCHAR(200) NULL,
                    CorrelationId  UNIQUEIDENTIFIER NULL,

                    DurationMs     BIGINT        NULL,
                    Summary        NVARCHAR(500) NULL,
                    Payload        NVARCHAR(MAX) NULL
                );
            END
            """;

        var indexDdl = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActionAudit_Time' AND object_id = OBJECT_ID('{schema}.ActionAudit'))
                CREATE INDEX IX_ActionAudit_Time ON {schema}.ActionAudit (OccurredAt DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActionAudit_Action' AND object_id = OBJECT_ID('{schema}.ActionAudit'))
                CREATE INDEX IX_ActionAudit_Action ON {schema}.ActionAudit (ActionType, OccurredAt DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActionAudit_Actor' AND object_id = OBJECT_ID('{schema}.ActionAudit'))
                CREATE INDEX IX_ActionAudit_Actor ON {schema}.ActionAudit (ActorId, OccurredAt DESC) WHERE ActorId IS NOT NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActionAudit_Scope' AND object_id = OBJECT_ID('{schema}.ActionAudit'))
                CREATE INDEX IX_ActionAudit_Scope ON {schema}.ActionAudit (ScopeKey, OccurredAt DESC) WHERE ScopeKey IS NOT NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActionAudit_Subject' AND object_id = OBJECT_ID('{schema}.ActionAudit'))
                CREATE INDEX IX_ActionAudit_Subject ON {schema}.ActionAudit (SubjectKind, SubjectId, OccurredAt DESC) WHERE SubjectId IS NOT NULL;
            """;

        await using (var cmd = new SqlCommand(tableDdl, connection, transaction))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        logger.LogDebug("M002: ActionAudit table ensured");

        await using (var cmd = new SqlCommand(indexDdl, connection, transaction))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        logger.LogDebug("M002: ActionAudit indexes ensured");
    }
}
