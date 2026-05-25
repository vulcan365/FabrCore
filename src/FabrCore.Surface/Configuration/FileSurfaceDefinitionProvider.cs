using FabrCore.Surface.Abstractions;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Configuration;

/// <summary>
/// Reads Surface definitions from a JSON file. Missing or malformed files fail closed.
/// </summary>
public sealed class FileSurfaceDefinitionProvider : ISurfaceDefinitionProvider
{
    private readonly string filePath;
    private readonly ILogger<FileSurfaceDefinitionProvider> logger;
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private List<SurfaceDefinition>? definitions;

    public FileSurfaceDefinitionProvider(
        SurfaceAiOptions options,
        ILogger<FileSurfaceDefinitionProvider> logger)
    {
        filePath = options.DefinitionFilePath;
        this.logger = logger;
    }

    public async Task<SurfaceDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(cancellationToken);
        return loaded.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<SurfaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
        => await LoadAsync(cancellationToken);

    private async Task<List<SurfaceDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (definitions is not null)
        {
            return definitions;
        }

        await loadLock.WaitAsync(cancellationToken);
        try
        {
            if (definitions is not null)
            {
                return definitions;
            }

            if (!File.Exists(filePath))
            {
                logger.LogInformation(
                    "Surface definition file not found at '{FilePath}', using an empty definition set.",
                    filePath);
                definitions = [];
                return definitions;
            }

            definitions = SurfaceDefinitionFileLoader.Load(filePath).ToList();

            logger.LogInformation(
                "Loaded {Count} Surface definitions from '{FilePath}'.",
                definitions.Count,
                filePath);

            return definitions;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Surface definitions from '{FilePath}'.", filePath);
            definitions = [];
            return definitions;
        }
        finally
        {
            loadLock.Release();
        }
    }
}
