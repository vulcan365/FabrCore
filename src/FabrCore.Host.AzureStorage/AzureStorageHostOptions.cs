namespace FabrCore.Host.AzureStorage;

/// <summary>
/// Azure Storage provider options, bound from the <c>Orleans:AzureStorage</c> configuration section.
/// All settings have sensible defaults — an empty section (or none at all) works out of the box.
/// </summary>
public class AzureStorageHostOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Orleans:AzureStorage";

    /// <summary>
    /// Backend for the <c>fabrcoreStorage</c> grain storage provider.
    /// Defaults to <see cref="AzureGrainStorageMode.Blob"/> because agent state (conversation
    /// histories) can easily exceed Azure Table's 1 MB entity limit. Use
    /// <see cref="AzureGrainStorageMode.Table"/> only when grain state is known to be small.
    /// </summary>
    public AzureGrainStorageMode GrainStorage { get; set; } = AzureGrainStorageMode.Blob;

    /// <summary>
    /// Blob container name for grain state when <see cref="GrainStorage"/> is Blob.
    /// </summary>
    public string ContainerName { get; set; } = "fabrcore-grainstate";

    /// <summary>
    /// Stream provider backend. Defaults to <see cref="AzureStreamMode.AzureQueue"/> — durable,
    /// works across silo restarts and multi-silo clusters. Use <see cref="AzureStreamMode.Memory"/>
    /// for lowest latency when message loss on silo failure is acceptable.
    /// </summary>
    public AzureStreamMode Streams { get; set; } = AzureStreamMode.AzureQueue;

    /// <summary>
    /// Number of Azure Storage queues backing the stream provider. Higher counts increase
    /// throughput/parallelism. All silos in the cluster must use the same value.
    /// </summary>
    public int StreamQueueCount { get; set; } = 8;
}

/// <summary>
/// Grain state storage backend for Azure Storage mode.
/// </summary>
public enum AzureGrainStorageMode
{
    /// <summary>Blob storage — one blob per grain, no practical state size limit (default).</summary>
    Blob,

    /// <summary>Table storage — lower cost for many small states, but limited to ~1 MB per grain.</summary>
    Table
}

/// <summary>
/// Stream provider backend for Azure Storage mode.
/// </summary>
public enum AzureStreamMode
{
    /// <summary>Azure Storage Queue streams — durable delivery (default).</summary>
    AzureQueue,

    /// <summary>In-memory streams — fastest, but messages are lost if a silo fails.</summary>
    Memory
}
