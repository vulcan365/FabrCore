using FabrCore.Host.Configuration;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using System.Text.Json;

namespace FabrCore.Host.Services;

public interface IOwnerScopedFabrCoreStorageProvider
{
    Task<T?> GetAsync<T>(string owner, string container, string entityKey, CancellationToken cancellationToken = default);
    Task UpsertAsync<T>(string owner, string container, string entityKey, T value, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string owner, string container, string entityKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// FabrCore entity storage backed by the configured Orleans grain storage provider.
/// Orleans types remain internal to the Host implementation.
/// </summary>
internal sealed class OrleansEntityStorageProvider : IFabrCoreStorageProvider, IOwnerScopedFabrCoreStorageProvider
{
    private const string EntityGrainType = "fabrcore.entity-storage";
    private const string DefaultOwner = "system";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IGrainStorage _storage;
    private readonly ILogger<OrleansEntityStorageProvider> _logger;

    public OrleansEntityStorageProvider(
        IServiceProvider serviceProvider,
        ILogger<OrleansEntityStorageProvider> logger)
    {
        _storage = serviceProvider.GetRequiredKeyedService<IGrainStorage>(
            FabrCoreOrleansConstants.StorageProviderName);
        _logger = logger;
    }

    public Task<T?> GetAsync<T>(string container, string entityKey, CancellationToken cancellationToken = default)
        => GetAsync<T>(DefaultOwner, container, entityKey, cancellationToken);

    public Task UpsertAsync<T>(string container, string entityKey, T value, CancellationToken cancellationToken = default)
        => UpsertAsync(DefaultOwner, container, entityKey, value, cancellationToken);

    public Task<bool> DeleteAsync(string container, string entityKey, CancellationToken cancellationToken = default)
        => DeleteAsync(DefaultOwner, container, entityKey, cancellationToken);

    public async Task<T?> GetAsync<T>(
        string owner,
        string container,
        string entityKey,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(owner, container, entityKey);

        var grainState = new SimpleGrainState<FabrCoreEntityStorageEnvelope>();
        await _storage.ReadStateAsync(container, BuildGrainId(owner, entityKey), grainState);

        if (!grainState.RecordExists || grainState.State is null)
        {
            _logger.LogDebug("Storage entity not found: {Owner}/{Container}/{EntityKey}", owner, container, entityKey);
            return default;
        }

        return JsonSerializer.Deserialize<T>(grainState.State.ValueJson, JsonOptions);
    }

    public async Task UpsertAsync<T>(
        string owner,
        string container,
        string entityKey,
        T value,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(owner, container, entityKey);

        var grainId = BuildGrainId(owner, entityKey);
        var grainState = new SimpleGrainState<FabrCoreEntityStorageEnvelope>();
        await _storage.ReadStateAsync(container, grainId, grainState);

        var now = DateTime.UtcNow;
        var envelope = new FabrCoreEntityStorageEnvelope
        {
            ValueJson = JsonSerializer.Serialize(value, JsonOptions),
            ValueType = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            CreatedUtc = grainState.RecordExists && grainState.State is not null
                ? grainState.State.CreatedUtc
                : now,
            UpdatedUtc = now
        };

        grainState.State = envelope;
        grainState.RecordExists = true;

        await _storage.WriteStateAsync(container, grainId, grainState);
        _logger.LogDebug("Storage entity upserted: {Owner}/{Container}/{EntityKey}", owner, container, entityKey);
    }

    public async Task<bool> DeleteAsync(
        string owner,
        string container,
        string entityKey,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(owner, container, entityKey);

        var grainId = BuildGrainId(owner, entityKey);
        var grainState = new SimpleGrainState<FabrCoreEntityStorageEnvelope>();
        await _storage.ReadStateAsync(container, grainId, grainState);

        if (!grainState.RecordExists)
        {
            _logger.LogDebug("Storage entity delete skipped because it did not exist: {Owner}/{Container}/{EntityKey}", owner, container, entityKey);
            return false;
        }

        await _storage.ClearStateAsync(container, grainId, grainState);
        _logger.LogDebug("Storage entity deleted: {Owner}/{Container}/{EntityKey}", owner, container, entityKey);
        return true;
    }

    private static GrainId BuildGrainId(string owner, string entityKey)
        => GrainId.Create(GrainType.Create(EntityGrainType), $"{owner}:{entityKey}");

    private static void ValidateAddress(string owner, string container, string entityKey)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner cannot be null or empty.", nameof(owner));
        if (string.IsNullOrWhiteSpace(container))
            throw new ArgumentException("Container cannot be null or empty.", nameof(container));
        if (string.IsNullOrWhiteSpace(entityKey))
            throw new ArgumentException("Entity key cannot be null or empty.", nameof(entityKey));
    }

    private sealed class SimpleGrainState<T> : IGrainState<T>
    {
        public T State { get; set; } = default!;
        public string? ETag { get; set; }
        public bool RecordExists { get; set; }
    }
}

[GenerateSerializer]
internal sealed class FabrCoreEntityStorageEnvelope
{
    [Id(0)]
    public string ValueJson { get; set; } = "null";

    [Id(1)]
    public string ValueType { get; set; } = string.Empty;

    [Id(2)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Id(3)]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
