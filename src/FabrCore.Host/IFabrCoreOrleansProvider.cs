using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host
{
    /// <summary>
    /// Pluggable Orleans clustering/persistence/reminders/streaming provider for
    /// <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>.
    /// <para>
    /// Implementations live in provider packages (e.g. <c>FabrCore.Host.SqlServer</c>,
    /// <c>FabrCore.Host.AzureStorage</c>) and are selected by <see cref="OrleansClusterOptions.ClusteringMode"/>.
    /// A provider is resolved in one of two ways:
    /// <list type="number">
    ///   <item>Explicit registration via <see cref="FabrCoreServerOptions.UseOrleansProvider"/>
    ///   (provider packages expose shorthand extensions such as <c>UseSqlServer()</c>).</item>
    ///   <item>Convention-based auto-discovery: an assembly named <c>FabrCore.Host.&lt;Mode&gt;</c>
    ///   containing a public parameterless implementation of this interface. Referencing the
    ///   provider NuGet package and setting <c>Orleans:ClusteringMode</c> is all that is required.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IFabrCoreOrleansProvider
    {
        /// <summary>
        /// The clustering mode this provider handles. Must match
        /// <see cref="OrleansClusterOptions.ClusteringMode"/> for the provider to be used.
        /// </summary>
        ClusteringMode Mode { get; }

        /// <summary>
        /// Auto-provisions backing resources (SQL tables, Azure tables/containers/queues, ...)
        /// before the silo starts. Called only when <see cref="OrleansClusterOptions.AutoInitDatabase"/>
        /// is true. Implementations should fail fast with a clear error when the configured
        /// connection cannot be reached.
        /// </summary>
        void Initialize(OrleansClusterOptions options, IConfiguration configuration, ILogger logger);

        /// <summary>
        /// Configures Orleans clustering, grain persistence, reminders, and streaming on the silo.
        /// Implementations must register:
        /// <list type="bullet">
        ///   <item>A grain storage provider named <see cref="FabrCoreOrleansConstants.StorageProviderName"/></item>
        ///   <item>A grain storage provider named <see cref="FabrCoreOrleansConstants.PubSubStoreName"/></item>
        ///   <item>A stream provider named <see cref="FabrCoreOrleansConstants.StreamProviderName"/></item>
        ///   <item>A reminder service</item>
        /// </list>
        /// </summary>
        void ConfigureSilo(ISiloBuilder siloBuilder, OrleansClusterOptions options, IConfiguration configuration, ILogger logger);
    }
}
