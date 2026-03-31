using FabrCore.Host.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FabrCore.Host.Services;

/// <summary>
/// Ensures the required Orleans SQL Server tables exist before the silo starts.
/// Executes embedded SQL scripts from the Orleans ADO.NET providers (v10.0.1).
/// </summary>
internal static class OrleansSqlServerInitializer
{
    // Scripts for the clustering database (ConnectionString)
    private static readonly string[] ClusteringScripts =
    [
        "SQLServer-Main.sql",
        "SQLServer-Clustering.sql",
        "SQLServer-Reminders.sql",
        "SQLServer-Streaming.sql"
    ];

    // Scripts for the storage database (EffectiveStorageConnectionString)
    private static readonly string[] StorageScripts =
    [
        "SQLServer-Main.sql",
        "SQLServer-Persistence.sql"
    ];

    // All scripts combined (when both databases are the same)
    private static readonly string[] AllScripts =
    [
        "SQLServer-Main.sql",
        "SQLServer-Clustering.sql",
        "SQLServer-Persistence.sql",
        "SQLServer-Reminders.sql",
        "SQLServer-Streaming.sql"
    ];

    /// <summary>
    /// Checks the configured SQL Server database(s) and creates any missing Orleans tables.
    /// </summary>
    internal static void EnsureOrleansTablesExist(OrleansClusterOptions options, ILogger logger)
    {
        if (options.ClusteringMode != ClusteringMode.SqlServer)
            return;

        var clusteringConnStr = options.ConnectionString;
        var storageConnStr = options.EffectiveStorageConnectionString;

        if (string.IsNullOrEmpty(clusteringConnStr))
        {
            logger.LogWarning("Cannot auto-initialize Orleans tables: ConnectionString is not configured");
            return;
        }

        if (AreSameDatabase(clusteringConnStr, storageConnStr))
        {
            logger.LogInformation("Initializing Orleans tables in single database");
            RunScripts(clusteringConnStr, AllScripts, logger);
        }
        else
        {
            logger.LogInformation("Initializing Orleans tables across clustering and storage databases");
            RunScripts(clusteringConnStr, ClusteringScripts, logger);

            if (!string.IsNullOrEmpty(storageConnStr))
            {
                RunScripts(storageConnStr, StorageScripts, logger);
            }
        }

        logger.LogInformation("Orleans SQL Server table initialization complete");
    }

    private static void RunScripts(string connectionString, string[] scriptNames, ILogger logger)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var dbName = connection.Database;

        foreach (var scriptName in scriptNames)
        {
            logger.LogDebug("Running Orleans SQL script: {ScriptName} against database {Database}", scriptName, dbName);

            var sql = ReadEmbeddedScript(scriptName);
            var batches = SplitOnGo(sql);

            foreach (var batch in batches)
            {
                using var cmd = new SqlCommand(batch, connection);
                cmd.CommandTimeout = 60;
                cmd.ExecuteNonQuery();
            }

            logger.LogDebug("Orleans SQL script {ScriptName} completed", scriptName);
        }
    }

    private static bool AreSameDatabase(string connStr1, string? connStr2)
    {
        if (string.IsNullOrEmpty(connStr2))
            return true;

        try
        {
            var builder1 = new SqlConnectionStringBuilder(connStr1);
            var builder2 = new SqlConnectionStringBuilder(connStr2);

            return string.Equals(builder1.DataSource, builder2.DataSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(builder1.InitialCatalog, builder2.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If we can't parse, assume different databases to be safe
            return false;
        }
    }

    private static string ReadEmbeddedScript(string scriptName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"FabrCore.Host.SqlScripts.{scriptName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] SplitOnGo(string sql)
    {
        return Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(batch => !string.IsNullOrWhiteSpace(batch))
            .ToArray();
    }
}
