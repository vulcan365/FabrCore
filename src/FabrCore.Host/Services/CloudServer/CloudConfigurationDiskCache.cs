using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace FabrCore.Host.Services.CloudServer;

/// <summary>
/// Persists the last successfully fetched configuration envelope to disk so the host can
/// start while the cloud server is unreachable. Disabled via
/// <see cref="CloudServerOptions.CacheLastKnownGood"/> (the cache holds API keys in plaintext,
/// the same exposure profile as fabrcore.json).
/// </summary>
internal sealed class CloudConfigurationDiskCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true
    };

    private readonly CloudServerOptions options;
    private readonly ILogger<CloudConfigurationDiskCache> logger;
    private readonly string cacheFilePath;

    public CloudConfigurationDiskCache(
        IOptions<CloudServerOptions> options,
        IWebHostEnvironment environment,
        ILogger<CloudConfigurationDiskCache> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        this.cacheFilePath = string.IsNullOrWhiteSpace(this.options.CacheFilePath)
            ? Path.Combine(environment.ContentRootPath, "fabrcore.cloud-cache.json")
            : this.options.CacheFilePath;
    }

    public string CacheFilePath => cacheFilePath;

    public async Task<CloudConfigurationEnvelope?> TryReadAsync(CancellationToken cancellationToken = default)
    {
        if (!options.CacheLastKnownGood || !File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken);
            return JsonSerializer.Deserialize<CloudConfigurationEnvelope>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to read cloud configuration cache at {Path}", cacheFilePath);
            return null;
        }
    }

    public async Task WriteAsync(CloudConfigurationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!options.CacheLastKnownGood)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(cacheFilePath, json, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write cloud configuration cache at {Path}", cacheFilePath);
        }
    }
}
