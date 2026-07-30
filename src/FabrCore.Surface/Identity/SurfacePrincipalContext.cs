namespace FabrCore.Surface.Identity;

public sealed record SurfacePrincipalContext(
    string? PrincipalId,
    string? DisplayName,
    bool IsAuthenticated,
    string? Source)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(PrincipalId);

    public static SurfacePrincipalContext Unresolved { get; } = new(null, null, false, null);
}
