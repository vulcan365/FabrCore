using FabrCore.Connections;

namespace FabrCore.Services.Connections;

/// <summary>Validates a client access token intended for FabrCore and resolves its stable principal. Never trust a broker-supplied principal string as user proof.</summary>
public interface IConnectionHandoffPrincipalValidator
{
    Task<string?> ValidateAsync(string userProof, CancellationToken cancellationToken);
}

internal sealed class ConnectionHandoffPrincipalValidator(ConnectionsOptions options, OAuthProvider oauth, IConnectionPrincipalResolver resolver)
    : IConnectionHandoffPrincipalValidator
{
    public async Task<string?> ValidateAsync(string userProof, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.HandoffAuthority) || string.IsNullOrWhiteSpace(options.HandoffAudience))
            throw new ConnectionException("feature-disabled", "Client handoff identity validation is not configured.");
        var principal = await oauth.Validate(new() { Authority = options.HandoffAuthority }, userProof, options.HandoffAudience, null, cancellationToken);
        // Entra app-only proof cannot stand in for a human authorizing a user connection.
        if (principal.FindFirst("idtyp")?.Value == "app" || string.IsNullOrWhiteSpace(principal.FindFirst("scp")?.Value)) return null;
        return resolver.Resolve(principal);
    }
}
