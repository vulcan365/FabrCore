namespace FabrCore.Core;

/// <summary>Stable, tenant-scoped identity shared by external agent transports.</summary>
public static class EntraPrincipalHandle
{
    /// <summary>Uses immutable Entra identifiers; application identities never alias users.</summary>
    public static string? Create(string? tenantId, string? objectId, bool application = false)
    {
        if (!Guid.TryParse(tenantId, out var tenant) || !Guid.TryParse(objectId, out var subject))
            return null;

        return $"{(application ? "app" : "entra")}-{tenant:D}-{subject:D}";
    }
}
