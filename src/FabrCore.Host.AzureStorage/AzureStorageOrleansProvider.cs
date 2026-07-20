using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Text;

namespace FabrCore.Host.AzureStorage;

/// <summary>
/// Azure Storage Orleans provider: table-based clustering and reminders, blob (default) or
/// table grain persistence, and Azure Storage Queue streams. Backing tables, containers, and
/// queues are created automatically on startup when
/// <see cref="OrleansClusterOptions.AutoInitDatabase"/> is true (the default).
/// <para>
/// For local development, point <c>Orleans:ConnectionString</c> at Azurite with
/// <c>UseDevelopmentStorage=true</c>.
/// </para>
/// </summary>
public sealed class AzureStorageOrleansProvider : IFabrCoreOrleansProvider
{
    public ClusteringMode Mode => ClusteringMode.AzureStorage;

    public void Initialize(OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
    {
        ValidateConnectionStrings(options);
        var azureOptions = BindAzureOptions(configuration);
        OrleansAzureStorageInitializer.EnsureResourcesExist(options, azureOptions, logger);
    }

    public void ConfigureSilo(ISiloBuilder siloBuilder, OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
    {
        ValidateConnectionStrings(options);
        var azureOptions = BindAzureOptions(configuration);
        var storageConnectionString = options.EffectiveStorageConnectionString!;

        // Clustering (Azure Table)
        siloBuilder.UseAzureStorageClustering(clustering =>
        {
            clustering.TableServiceClient = new TableServiceClient(options.ConnectionString);
        });
        siloBuilder.Configure<ClusterOptions>(cluster =>
        {
            cluster.ClusterId = options.ClusterId;
            cluster.ServiceId = options.ServiceId;
        });
        logger.LogInformation("Orleans Azure Storage clustering configured - ClusterId: {ClusterId}, ServiceId: {ServiceId}",
            options.ClusterId, options.ServiceId);

        // Grain persistence for FabrCore state — blob by default (agent conversation state can
        // exceed Azure Table's 1 MB entity limit), table opt-in via Orleans:AzureStorage:GrainStorage.
        if (azureOptions.GrainStorage == AzureGrainStorageMode.Blob)
        {
            siloBuilder.AddAzureBlobGrainStorage(FabrCoreOrleansConstants.StorageProviderName, storage =>
            {
                storage.BlobServiceClient = new BlobServiceClient(storageConnectionString);
                storage.ContainerName = azureOptions.ContainerName;
            });
            logger.LogInformation("Orleans Azure Blob grain persistence configured (container: {ContainerName})",
                azureOptions.ContainerName);
        }
        else
        {
            siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.StorageProviderName, storage =>
            {
                storage.TableServiceClient = new TableServiceClient(storageConnectionString);
            });
            logger.LogInformation("Orleans Azure Table grain persistence configured");
        }

        // Stream pub/sub state is small — always table storage.
        siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.PubSubStoreName, storage =>
        {
            storage.TableServiceClient = new TableServiceClient(storageConnectionString);
        });

        // Reminders (Azure Table)
        siloBuilder.UseAzureTableReminderService(reminders =>
        {
            reminders.TableServiceClient = new TableServiceClient(options.ConnectionString);
        });
        logger.LogInformation("Orleans Azure Storage reminders configured");

        // Streaming — Azure Storage Queues by default for durable delivery.
        if (azureOptions.Streams == AzureStreamMode.AzureQueue)
        {
            var queueNames = GetStreamQueueNames(options.ServiceId, azureOptions.StreamQueueCount);
            siloBuilder.AddAzureQueueStreams(FabrCoreOrleansConstants.StreamProviderName, configurator =>
            {
                configurator.ConfigureAzureQueue(queueOptions => queueOptions.Configure(queue =>
                {
                    queue.QueueServiceClient = new QueueServiceClient(storageConnectionString);
                    queue.QueueNames = queueNames;
                }));
            });
            logger.LogInformation("Orleans Azure Queue streams configured ({QueueCount} queues)", queueNames.Count);
        }
        else
        {
            siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
            logger.LogDebug("Orleans memory streams configured");
        }
    }

    private static void ValidateConnectionStrings(OrleansClusterOptions options)
    {
        if (string.IsNullOrEmpty(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "Orleans:ConnectionString is required when using AzureStorage clustering mode. " +
                "Use an Azure Storage account connection string, or 'UseDevelopmentStorage=true' for local Azurite.");
        }
    }

    internal static AzureStorageHostOptions BindAzureOptions(IConfiguration configuration)
        => configuration.GetSection(AzureStorageHostOptions.SectionName).Get<AzureStorageHostOptions>()
            ?? new AzureStorageHostOptions();

    /// <summary>
    /// Deterministic stream queue names derived from the service id. All silos in a cluster
    /// compute the same list. Azure queue names must be 3-63 chars, lowercase alphanumeric
    /// with single dashes, starting and ending with a letter or number.
    /// </summary>
    internal static List<string> GetStreamQueueNames(string serviceId, int queueCount)
    {
        if (queueCount < 1)
        {
            throw new InvalidOperationException("Orleans:AzureStorage:StreamQueueCount must be at least 1.");
        }

        var prefix = SanitizeQueueName($"{serviceId}-streams");
        return Enumerable.Range(0, queueCount)
            .Select(i => $"{prefix}-{i}")
            .ToList();
    }

    private static string SanitizeQueueName(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastWasDash = true; // suppress leading dashes

        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var sanitized = sb.ToString().TrimEnd('-');
        if (sanitized.Length < 3)
        {
            sanitized = $"fabrcore-{sanitized}".TrimEnd('-');
        }

        // Leave room for the "-{index}" suffix within the 63-char limit.
        return sanitized.Length > 58 ? sanitized[..58].TrimEnd('-') : sanitized;
    }
}
