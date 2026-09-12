using FabrCore.Host.Services;
using FabrCore.Sdk;
using FabrCore.Services.GraphRag;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Database;

internal sealed class DatabaseSchemaHostedService(
    FabrCoreDatabaseOptions options,
    IConfiguration configuration,
    AgentMemoryOptions memory,
    SqlAclRepository acl,
    OperationalDatabase operations,
    IFabrCoreModelConfigurationResolver models,
    ILogger<DatabaseSchemaHostedService> logger) : IHostedService
{
    internal static string Connection(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value ? value :
        throw new InvalidOperationException($"ConnectionStrings:{name} is required for SQL mode.");

    public async Task StartAsync(CancellationToken ct)
    {
        await operations.InitializeAsync(options.AutoInitialize, ct);
        // Resolve configuration and credentials without issuing a paid model request.
        var modelNames = new[] { "embeddings", memory.Models.RelevanceModelName, memory.Models.CompactionModelName,
            memory.Models.ImaginingModelName, memory.Models.PlannerModelName, memory.Models.SmallModelName, memory.Models.LargeModelName };
        foreach (var name in modelNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            await ValidateModel(name, ct);
        if (configuration.GetValue("GraphRag:Ingestion:EnableExtraction", true))
        {
            var name = configuration["FabrCore:GraphRag:ExtractionModelName"];
            if (string.IsNullOrWhiteSpace(name))
            {
                try { await models.GetModelConfigurationAsync("graphrag", ct); name = "graphrag"; }
                catch (KeyNotFoundException) { name = "default"; }
            }
            await ValidateModel(name, ct);
        }
        if (memory.EmbeddingDimensions != 1536)
            throw new InvalidOperationException("The combined SQL feature set requires 1536-dimensional embeddings, matching the existing GraphRAG schema.");

        var memoryConnection = Connection(configuration, memory.ConnectionStringName);
        var graphConnection = Connection(configuration, options.GraphRagConnectionStringName ?? options.ConnectionStringName);
        if (options.AutoInitialize)
        {
            await InitializeAcl(acl.ConnectionString, ct);
            await MemorySchemaInitializer.EnsureSchemaAsync(memoryConnection, memory.EmbeddingDimensions, logger);
            await GraphRagSchemaInitializer.EnsureSchemaAsync(graphConnection, logger);
        }
        await ValidateTables(acl.ConnectionString, ["acl.Configuration", "acl.Principal", "acl.Role", "acl.Group", "acl.PrincipalRole", "acl.GroupRole", "acl.GroupMember", "acl.PermissionGrant"], ct);
        await ValidateTables(memoryConnection, ["mem.MemoryEntity", "mem.MemoryChunk", "mem.MemoryRelationship", "mem.MemorySummaryNode", "mem.MemoryScope", "mem.MemoryAuditLog", "mem.MemoryExtractionReceipt"], ct);
        await ValidateTables(graphConnection, ["grag.SchemaVersion", "grag.KnowledgeEntity", "grag.KnowledgeRelationship", "grag.KnowledgeScope"], ct);
        await using var connection = new SqlConnection(graphConnection);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT Version FROM grag.SchemaVersion", connection);
        var applied = new HashSet<long>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) applied.Add(reader.GetInt64(0));
        if (FabrCore.Services.GraphRag.Migrations.Migrations.Registered.Any(m => !applied.Contains(m.Version)))
            throw new InvalidOperationException("GraphRAG migrations are pending. Provision the schema or enable FabrCore:Database:AutoInitialize.");
        logger.LogInformation("FabrCore SQL features initialized: ACL, Memory, GraphRAG, security audit, execution evidence, A2A tasks. ACL snapshot loads after Orleans startup.");
    }

    private async Task ValidateModel(string name, CancellationToken ct)
    {
        var model = await models.GetModelConfigurationAsync(name, ct);
        if (!string.IsNullOrWhiteSpace(model.ApiKeyAlias)) await models.GetApiKeyAsync(model.ApiKeyAlias, ct);
    }

    private static async Task InitializeAcl(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource='FabrCore.Acl.Schema', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=60000;
            IF @result < 0 THROW 51000, 'Could not acquire ACL schema lock.', 1;
            """ + SqlAclRepository.SchemaSql, connection, tx) { CommandTimeout = 90 };
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task ValidateTables(string connectionString, string[] tables, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        // Verify vector support even for an already provisioned database.
        await using (var vector = new SqlCommand("DECLARE @v VECTOR(1536);", connection)) await vector.ExecuteNonQueryAsync(ct);
        foreach (var table in tables)
        {
            await using var cmd = new SqlCommand("SELECT OBJECT_ID(@table, 'U')", connection);
            cmd.Parameters.AddWithValue("@table", table);
            if (await cmd.ExecuteScalarAsync(ct) is null or DBNull)
                throw new InvalidOperationException($"Required table '{table}' is missing. Provision the database or enable FabrCore:Database:AutoInitialize.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class DatabaseReadinessCheck(FabrCoreDatabaseOptions options, IConfiguration configuration, GrainBackedAclEntityStore acl) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!acl.IsReady) return HealthCheckResult.Unhealthy("ACL snapshot is not ready.");
        try
        {
            foreach (var name in new[] { options.ConnectionStringName, options.AclConnectionStringName, options.MemoryConnectionStringName, options.GraphRagConnectionStringName, options.OperationsConnectionStringName }.OfType<string>().Distinct())
            {
                await using var connection = new SqlConnection(DatabaseSchemaHostedService.Connection(configuration, name));
                await connection.OpenAsync(cancellationToken);
            }
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("A FabrCore feature database is unavailable.", ex); }
    }
}
