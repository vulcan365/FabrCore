using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace FabrCore.Host.SqlServer;

/// <summary>
/// SQL Server (ADO.NET) Orleans provider: clustering, grain persistence, and reminders
/// backed by SQL Server, with memory streams (Orleans has no SQL Server streaming provider).
/// Required Orleans tables are created automatically on startup when
/// <see cref="OrleansClusterOptions.AutoInitDatabase"/> is true (the default).
/// </summary>
public sealed class SqlServerOrleansProvider : IFabrCoreOrleansProvider
{
    private const string AdoNetInvariant = "Microsoft.Data.SqlClient";

    public ClusteringMode Mode => ClusteringMode.SqlServer;

    public void Initialize(OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
    {
        logger.LogInformation("Auto-initializing Orleans SQL Server database tables");
        OrleansSqlServerInitializer.EnsureOrleansTablesExist(options, logger);
    }

    public void ConfigureSilo(ISiloBuilder siloBuilder, OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
    {
        if (string.IsNullOrEmpty(options.ConnectionString))
        {
            throw new InvalidOperationException("Orleans:ConnectionString is required when using SqlServer clustering mode.");
        }

        var storageConnectionString = options.EffectiveStorageConnectionString;
        if (string.IsNullOrEmpty(storageConnectionString))
        {
            throw new InvalidOperationException("Orleans:ConnectionString or Orleans:StorageConnectionString is required when using SqlServer clustering mode.");
        }

        // Clustering
        siloBuilder.UseAdoNetClustering(clustering =>
        {
            clustering.Invariant = AdoNetInvariant;
            clustering.ConnectionString = options.ConnectionString;
        });
        siloBuilder.Configure<ClusterOptions>(cluster =>
        {
            cluster.ClusterId = options.ClusterId;
            cluster.ServiceId = options.ServiceId;
        });
        logger.LogInformation("Orleans SQL Server clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
            options.ClusterId, options.ServiceId);

        // Grain persistence
        siloBuilder.AddAdoNetGrainStorage(FabrCoreOrleansConstants.StorageProviderName, storage =>
        {
            storage.Invariant = AdoNetInvariant;
            storage.ConnectionString = storageConnectionString;
        });
        siloBuilder.AddAdoNetGrainStorage(FabrCoreOrleansConstants.PubSubStoreName, storage =>
        {
            storage.Invariant = AdoNetInvariant;
            storage.ConnectionString = storageConnectionString;
        });
        logger.LogInformation("Orleans SQL Server grain persistence configured");

        // Reminders
        siloBuilder.UseAdoNetReminderService(reminders =>
        {
            reminders.Invariant = AdoNetInvariant;
            reminders.ConnectionString = options.ConnectionString;
        });
        logger.LogInformation("Orleans SQL Server reminders configured");

        // Streaming — no SQL Server streaming provider exists, use memory streams
        siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
        logger.LogDebug("Orleans memory streams configured");
    }
}
