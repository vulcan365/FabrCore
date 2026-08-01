namespace FabrCore.Surface.Identity;

public interface ISurfacePrincipalContextProvider
{
    Task<SurfacePrincipalContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}
