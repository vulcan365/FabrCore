namespace FabrCore.Host.AzureStorage;

/// <summary>
/// Extension methods for wiring the Azure Storage Orleans provider into a FabrCore server.
/// </summary>
public static class FabrCoreAzureStorageExtensions
{
    /// <summary>
    /// Uses Azure Storage for Orleans clustering (tables), grain persistence (blob by default),
    /// reminders (tables), and streaming (queues).
    /// <para>
    /// Calling this is optional — referencing the FabrCore.Host.AzureStorage package and setting
    /// <c>Orleans:ClusteringMode</c> to <c>AzureStorage</c> is enough for the provider to be
    /// auto-discovered by <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>.
    /// </para>
    /// </summary>
    public static FabrCoreServerOptions UseAzureStorage(this FabrCoreServerOptions options)
        => options.UseOrleansProvider(new AzureStorageOrleansProvider());
}
