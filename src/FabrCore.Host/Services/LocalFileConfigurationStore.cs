using FabrCore.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FabrCore.Host.Services;

/// <summary>
/// Default <see cref="IFabrCoreConfigurationStore"/> backed by fabrcore.json in the content
/// root. Preserves the original host behavior: a missing file is created empty on first read.
/// </summary>
public sealed class LocalFileConfigurationStore : IFabrCoreConfigurationStore
{
    private readonly ILogger<LocalFileConfigurationStore> logger;
    private readonly string configFilePath;

    public LocalFileConfigurationStore(ILogger<LocalFileConfigurationStore> logger, IWebHostEnvironment env)
    {
        this.logger = logger;
        this.configFilePath = Path.Combine(env.ContentRootPath, "fabrcore.json");
    }

    public bool SupportsWrites => true;

    public async Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configFilePath))
        {
            logger.LogWarning("Configuration file {Path} not found. Creating default configuration.", configFilePath);
            var defaultConfig = new FabrCoreConfiguration();
            await SaveConfigurationAsync(defaultConfig, cancellationToken);
            return defaultConfig;
        }

        var json = await File.ReadAllTextAsync(configFilePath, cancellationToken);
        return JsonSerializer.Deserialize<FabrCoreConfiguration>(json) ?? new FabrCoreConfiguration();
    }

    public async Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(configFilePath, json, cancellationToken);
    }
}
