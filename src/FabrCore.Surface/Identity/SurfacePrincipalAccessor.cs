namespace FabrCore.Surface.Identity;

/// <summary>
/// Scoped ambient principal override. Hosts that already know the current principal
/// (for example from an existing circuit user context) set <see cref="Principal"/> and the
/// default provider returns it ahead of header and claims resolution.
/// </summary>
public sealed class SurfacePrincipalAccessor
{
    public SurfacePrincipalContext? Principal { get; set; }
}
