using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Maps an authenticated A2A caller onto the FabrCore principal its agents run as.
/// </summary>
/// <remarks>
/// Register your own singleton implementation <b>before <c>AddFabrCoreServer</c></b> to map callers
/// however your deployment needs — for example, one principal per tenant claim. The host registers
/// its default with <c>TryAdd</c>, so a resolver registered afterwards is silently ignored and every
/// caller runs as the default principal.
/// </remarks>
public interface IA2APrincipalResolver
{
    /// <summary>
    /// Returns the principal handle for this request, or null when the caller cannot be mapped
    /// (which the endpoint reports as an authorization failure).
    /// </summary>
    /// <remarks>
    /// Asynchronous because mapping a caller to a real user usually means consulting a directory
    /// or store — which is the whole point of the per-caller strategies. Implementations that need
    /// no I/O can return a completed <see cref="ValueTask{TResult}"/> at no cost.
    /// </remarks>
    ValueTask<string?> ResolvePrincipalHandleAsync(
        HttpContext context,
        A2AExposedAgent agent,
        string contextId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A short label for the caller, recorded on messages and in logs. Never a secret. Synchronous
    /// because it only reads claims already on the request.
    /// </summary>
    string? DescribeCaller(HttpContext context);
}

internal sealed class DefaultA2APrincipalResolver : IA2APrincipalResolver
{
    private readonly A2AOptions _options;
    private readonly ILogger<DefaultA2APrincipalResolver> _logger;

    public DefaultA2APrincipalResolver(
        IOptions<A2AOptions> options, ILogger<DefaultA2APrincipalResolver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ValueTask<string?> ResolvePrincipalHandleAsync(
        HttpContext context,
        A2AExposedAgent agent,
        string contextId,
        CancellationToken cancellationToken = default)
    {
        var principal = _options.Principal;
        var raw = principal.Strategy switch
        {
            A2APrincipalStrategy.Fixed => principal.Handle,
            A2APrincipalStrategy.ContextId => contextId,
            A2APrincipalStrategy.ApiKey => context.User.FindFirstValue(A2AClaimTypes.PrincipalHandle),
            A2APrincipalStrategy.Claim => context.User.FindFirstValue(_options.Authentication.JwtBearer.PrincipalClaimType),
            _ => principal.Handle,
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning(
                "Could not map an A2A caller to a FabrCore principal using strategy {Strategy} for agent {Agent}.",
                principal.Strategy, agent.Name);
            return ValueTask.FromResult<string?>(null);
        }

        var handle = A2AAgentCatalog.Slug(raw);
        if (handle.Length == 0)
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(
            string.IsNullOrWhiteSpace(principal.Prefix) ? handle : principal.Prefix + handle);
    }

    public string? DescribeCaller(HttpContext context)
        => context.User.FindFirstValue(A2AClaimTypes.ApiKeyName)
           ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.FindFirstValue("sub");
}
