using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Migrations;

/// <summary>
/// Forward-only schema migration runner for the GraphRAG <c>grag.*</c>
/// database. On each startup it:
/// <list type="number">
///   <item>Acquires an exclusive session-scoped <c>sp_getapplock</c> so only one silo applies migrations at a time.</item>
///   <item>Ensures the <c>grag</c> schema exists.</item>
///   <item>Ensures <c>grag.SchemaVersion</c> exists (bootstrapped here, not via a migration — chicken-and-egg).</item>
///   <item>Reads applied version numbers, runs each pending migration from <see cref="Migrations.Registered"/> in order, each in its own transaction, recording success in <c>grag.SchemaVersion</c>.</item>
///   <item>Releases the applock.</item>
/// </list>
///
/// On any migration failure, startup throws and the partial migration is rolled
/// back. The next startup retries from the failing version.
/// </summary>
public static class GraphRagMigrationRunner
{
    /// <summary>
    /// Resource name used for the cross-silo applock. Scoped to the GraphRAG
    /// schema so unrelated systems on the same database (e.g. expmem) cannot
    /// block each other.
    /// </summary>
    internal const string ApplockResource = "grag:schema-migration";

    /// <summary>
    /// Maximum time (ms) the runner will wait to acquire the applock before
    /// giving up. 60s comfortably covers the case where a sibling silo is
    /// part-way through a slow migration.
    /// </summary>
    internal const int ApplockTimeoutMs = 60_000;

    /// <summary>
    /// Runs every pending migration in <see cref="Migrations.Registered"/>
    /// against the supplied database. Idempotent — calling twice in a row from
    /// a freshly-deployed binary applies migrations on the first call and is a
    /// no-op on the second.
    /// </summary>
    public static Task RunMigrationsAsync(string connectionString, ILogger? logger = null)
        => RunMigrationsAsync(connectionString, logger, CancellationToken.None);

    public static async Task RunMigrationsAsync(string connectionString, ILogger? logger, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Serialize bootstrap too: IF-NOT-EXISTS + CREATE SCHEMA is not atomic,
        // so concurrent first-start silos can otherwise race before the
        // SchemaVersion table exists.
        try
        {
            await AcquireApplockAsync(connection, logger, cancellationToken);
            await ExecuteAsync(connection, GraphRagSchemaInitializer.GetSchemaDdl(),
                "ensuring grag schema", logger, cancellationToken);

            // SchemaVersion backs the runner and is therefore bootstrapped
            // outside the migration registry, but still under the applock.
            await ExecuteAsync(connection, GetSchemaVersionTableDdl(),
                "ensuring grag.SchemaVersion", logger, cancellationToken);

            var applied = await LoadAppliedVersionsAsync(connection, cancellationToken);
            var pending = Migrations.Registered
                .Where(m => !applied.Contains(m.Version))
                .OrderBy(m => m.Version)
                .ToList();

            if (pending.Count == 0)
            {
                logger?.LogInformation("GraphRAG schema is up to date (latest version: {Version})",
                    applied.Count == 0 ? 0 : applied.Max());
                return;
            }

            logger?.LogInformation(
                "GraphRAG schema: applying {Count} pending migration(s): {Versions}",
                pending.Count, string.Join(", ", pending.Select(m => m.Version)));

            foreach (var migration in pending)
            {
                await ApplyOneAsync(connection, migration, logger, cancellationToken);
            }

            logger?.LogInformation(
                "GraphRAG schema migration complete (current version: {Version})",
                pending[^1].Version);
        }
        catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("GraphRAG schema initialization was canceled.", ex, cancellationToken);
        }
        finally
        {
            await ReleaseApplockAsync(connection, logger);
        }
    }

    // ─── Internal helpers ────────────────────────────────────────────────

    private static string GetSchemaVersionTableDdl() => $$"""
        IF OBJECT_ID('{{GraphRagSchemaInitializer.SchemaName}}.SchemaVersion', 'U') IS NULL
        BEGIN
            CREATE TABLE {{GraphRagSchemaInitializer.SchemaName}}.SchemaVersion (
                Version       BIGINT        NOT NULL PRIMARY KEY,
                Description   NVARCHAR(500) NOT NULL,
                AppliedAt     DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
                DurationMs    INT           NOT NULL,
                AppliedBy     NVARCHAR(128) NOT NULL DEFAULT SUSER_SNAME()
            );
        END
        """;

    private static async Task ExecuteAsync(
        SqlConnection conn, string sql, string description, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "GraphRAG migration runner: failed while {Description}", description);
            throw;
        }
    }

    private static async Task AcquireApplockAsync(SqlConnection conn, ILogger? logger, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("sp_getapplock", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 90
        };
        cmd.Parameters.AddWithValue("@Resource", ApplockResource);
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", ApplockTimeoutMs);
        var ret = new SqlParameter("@ret", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.ReturnValue
        };
        cmd.Parameters.Add(ret);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        var code = (int)ret.Value!;
        // 0 = granted immediately, 1 = granted after wait, negative = error/timeout.
        if (code < 0)
        {
            throw new InvalidOperationException(
                $"GraphRAG migration runner could not acquire applock '{ApplockResource}' " +
                $"(sp_getapplock returned {code}). Another silo may be running migrations or holding the lock.");
        }
        logger?.LogDebug("GraphRAG migration applock acquired (code {Code})", code);
    }

    private static async Task ReleaseApplockAsync(SqlConnection conn, ILogger? logger)
    {
        try
        {
            await using var cmd = new SqlCommand(
                "IF APPLOCK_MODE('public', @Resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource=@Resource, @LockOwner=@LockOwner", conn);
            cmd.Parameters.AddWithValue("@Resource", ApplockResource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            logger?.LogDebug("GraphRAG migration applock released");
        }
        catch (Exception ex)
        {
            // Discard a session whose lock state could not be confirmed.
            SqlConnection.ClearPool(conn);
            logger?.LogWarning(ex, "GraphRAG migration applock release failed (non-fatal)");
        }
    }

    private static async Task<HashSet<long>> LoadAppliedVersionsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        var sql = $"SELECT Version FROM {GraphRagSchemaInitializer.SchemaName}.SchemaVersion";
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var set = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken))
            set.Add(reader.GetInt64(0));
        return set;
    }

    internal static async Task ApplyOneAsync(
        SqlConnection conn, IGraphRagMigration migration, ILogger? logger, CancellationToken cancellationToken)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        try
        {
            logger?.LogInformation("GraphRAG migration: applying version {Version} — {Description}",
                migration.Version, migration.Description);

            await migration.ApplyAsync(conn, tx, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cancellationToken);

            sw.Stop();

            var insertSql = $"""
                INSERT INTO {GraphRagSchemaInitializer.SchemaName}.SchemaVersion
                    (Version, Description, DurationMs)
                VALUES (@version, @description, @durationMs);
                """;
            await using (var insert = new SqlCommand(insertSql, conn, tx))
            {
                insert.Parameters.AddWithValue("@version", migration.Version);
                insert.Parameters.AddWithValue("@description", migration.Description);
                insert.Parameters.AddWithValue("@durationMs", (int)sw.ElapsedMilliseconds);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            logger?.LogInformation(
                "GraphRAG migration: version {Version} applied in {Duration}ms",
                migration.Version, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            try { await tx.RollbackAsync(CancellationToken.None); } catch { SqlConnection.ClearPool(conn); }
            logger?.LogError(ex,
                "GraphRAG migration: version {Version} ({Description}) FAILED after {Duration}ms — startup will abort and retry on next launch",
                migration.Version, migration.Description, sw.ElapsedMilliseconds);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("GraphRAG migration was canceled and rolled back.", ex, cancellationToken);
            throw;
        }
    }
}
