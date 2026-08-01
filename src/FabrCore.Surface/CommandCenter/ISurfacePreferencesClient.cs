using FabrCore.Surface.Services;

namespace FabrCore.Surface.CommandCenter;

public interface ISurfacePreferencesClient
{
    Task<SurfacePreferences> GetAsync(
        string principalId,
        SurfaceOptions defaults,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string principalId,
        SurfacePreferences preferences,
        CancellationToken cancellationToken = default);
}
