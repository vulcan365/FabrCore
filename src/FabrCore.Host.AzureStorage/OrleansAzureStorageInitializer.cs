using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.AzureStorage;

/// <summary>
/// Pre-provisions the Azure Storage resources Orleans needs (tables, blob container, stream
/// queues) before the silo starts. Orleans providers create most of these lazily on first use,
/// but provisioning upfront gives a fast, clear failure when the connection string is wrong
/// and avoids first-request latency — the same zero-setup experience as SqlServer mode.
/// </summary>
internal static class OrleansAzureStorageInitializer
{
    // Default table names used by the Orleans Azure Storage providers.
    private const string ClusteringTableName = "OrleansSiloInstances";
    private const string RemindersTableName = "OrleansReminders";
    private const string GrainStateTableName = "OrleansGrainState";

    internal static void EnsureResourcesExist(OrleansClusterOptions options, AzureStorageHostOptions azureOptions, ILogger logger)
    {
        var clusteringConnStr = options.ConnectionString!;
        var storageConnStr = options.EffectiveStorageConnectionString!;

        try
        {
            logger.LogInformation("Provisioning Azure Storage resources for Orleans");

            // Clustering + reminders tables live on the clustering connection string.
            var clusteringTables = new TableServiceClient(clusteringConnStr);
            clusteringTables.CreateTableIfNotExists(ClusteringTableName);
            clusteringTables.CreateTableIfNotExists(RemindersTableName);
            logger.LogDebug("Orleans clustering and reminders tables ensured");

            // Grain state (fabrcoreStorage + PubSubStore) lives on the storage connection string.
            var storageTables = new TableServiceClient(storageConnStr);
            storageTables.CreateTableIfNotExists(GrainStateTableName);

            if (azureOptions.GrainStorage == AzureGrainStorageMode.Blob)
            {
                var container = new BlobContainerClient(storageConnStr, azureOptions.ContainerName);
                container.CreateIfNotExists();
                logger.LogDebug("Orleans grain state blob container '{ContainerName}' ensured", azureOptions.ContainerName);
            }

            if (azureOptions.Streams == AzureStreamMode.AzureQueue)
            {
                var queueNames = AzureStorageOrleansProvider.GetStreamQueueNames(options.ServiceId, azureOptions.StreamQueueCount);
                foreach (var queueName in queueNames)
                {
                    new QueueClient(storageConnStr, queueName).CreateIfNotExists();
                }
                logger.LogDebug("Orleans stream queues ensured ({QueueCount} queues)", queueNames.Count);
            }

            logger.LogInformation("Azure Storage resources for Orleans provisioned");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to provision Azure Storage resources for Orleans. Verify that Orleans:ConnectionString " +
                "(and Orleans:StorageConnectionString, if set) contains a valid Azure Storage connection string " +
                "and that the account is reachable. For local development, run Azurite and use " +
                "'UseDevelopmentStorage=true'. To skip auto-provisioning, set Orleans:AutoInitDatabase to false.",
                ex);
        }
    }
}
