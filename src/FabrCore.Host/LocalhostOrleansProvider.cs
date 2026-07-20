using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host
{
    /// <summary>
    /// Built-in Localhost mode: in-memory clustering, grain storage, reminders, and streams.
    /// For development only — all state is lost when the process exits.
    /// </summary>
    public sealed class LocalhostOrleansProvider : IFabrCoreOrleansProvider
    {
        public ClusteringMode Mode => ClusteringMode.Localhost;

        public void Initialize(OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
        {
            // Nothing to provision — everything is in-memory.
        }

        public void ConfigureSilo(ISiloBuilder siloBuilder, OrleansClusterOptions options, IConfiguration configuration, ILogger logger)
        {
            siloBuilder.UseLocalhostClustering();
            logger.LogDebug("Orleans localhost clustering configured");

            siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.StorageProviderName);
            siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.PubSubStoreName);
            logger.LogWarning(
                "Orleans Localhost mode uses in-memory grain storage for {StorageProviderName}. " +
                "FabrCore typed storage entities, agent state, client state, and management state will be lost when the process exits. " +
                "Use SqlServer, AzureStorage, or custom Orleans storage for restart-safe persistence.",
                FabrCoreOrleansConstants.StorageProviderName);

            siloBuilder.UseInMemoryReminderService();
            logger.LogDebug("Orleans in-memory reminder service configured");

            siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
            logger.LogDebug("Orleans memory streams configured");
        }
    }
}
