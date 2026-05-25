using FabrCore.Surface.Configuration;

namespace FabrCore.Surface.Abstractions;

/// <summary>
/// Loads named Surface generation profiles from <c>fabrcore-surface.json</c>.
/// </summary>
public interface ISurfaceDefinitionProvider
{
    Task<SurfaceDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SurfaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
}
