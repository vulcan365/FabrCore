using System.Security.Claims;
using FabrCore.Surface.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.Identity;

/// <summary>
/// Default principal resolution chain: host resolver delegate, ambient
/// <see cref="SurfacePrincipalAccessor"/>, state persisted from prerender, configured
/// request headers, claims (circuit-safe via <see cref="AuthenticationStateProvider"/>
/// when available), then development fallback. Fails closed to
/// <see cref="SurfacePrincipalContext.Unresolved"/>.
/// </summary>
public sealed class DefaultSurfacePrincipalContextProvider : ISurfacePrincipalContextProvider, IDisposable
{
    public const string PersistenceKey = "FabrCore.Surface.PrincipalContext";

    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly SurfaceOptions options;
    private readonly SurfacePrincipalAccessor principalAccessor;
    private readonly IServiceProvider serviceProvider;

    private SurfacePrincipalContext? memoized;
    private SurfacePrincipalContext? pendingPersist;
    private PersistingComponentStateSubscription persistingSubscription;
    private bool persistenceRegistered;

    public DefaultSurfacePrincipalContextProvider(
        IHttpContextAccessor httpContextAccessor,
        IOptions<SurfaceOptions> options,
        SurfacePrincipalAccessor principalAccessor,
        IServiceProvider? serviceProvider = null)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.options = options.Value;
        this.principalAccessor = principalAccessor;
        this.serviceProvider = serviceProvider ?? EmptyServiceProvider.Instance;
    }

    public async Task<SurfacePrincipalContext> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options.PrincipalResolver is { } resolver)
        {
            var resolved = await resolver(serviceProvider, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                return Normalize(resolved.Source is null ? resolved with { Source = "resolver" } : resolved);
            }
        }

        if (principalAccessor.Principal is { IsResolved: true } ambient)
        {
            return Normalize(ambient.Source is null ? ambient with { Source = "ambient" } : ambient);
        }

        if (memoized is not null)
        {
            return memoized;
        }

        // Prerender and circuit run in different DI scopes, so header identity resolved
        // against the prerender HttpContext is replayed in the circuit via persisted state.
        var persisted = TakePersisted();
        if (persisted is { IsResolved: true })
        {
            var restored = Normalize(persisted);
            memoized = restored;
            return restored;
        }

        var httpContext = httpContextAccessor.HttpContext;

        var fromHeader = ResolveFromHeaders(httpContext);
        if (fromHeader is not null)
        {
            return CacheAuthenticated(Normalize(fromHeader));
        }

        var fromClaims = await ResolveFromClaimsAsync(httpContext).ConfigureAwait(false);
        if (fromClaims is not null)
        {
            return CacheAuthenticated(Normalize(fromClaims));
        }

        if (!string.IsNullOrWhiteSpace(options.DevelopmentFallbackPrincipalId))
        {
            return Normalize(new SurfacePrincipalContext(
                options.DevelopmentFallbackPrincipalId,
                options.DevelopmentFallbackPrincipalId,
                false,
                nameof(SurfaceOptions.DevelopmentFallbackPrincipalId)));
        }

        return SurfacePrincipalContext.Unresolved;
    }

    public void Dispose() => persistingSubscription.Dispose();

    private SurfacePrincipalContext? ResolveFromHeaders(HttpContext? httpContext)
    {
        if (httpContext is null || options.PrincipalHeaderNames.Count == 0)
        {
            return null;
        }

        foreach (var headerName in options.PrincipalHeaderNames.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var value = httpContext.Request.Headers[headerName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var principalId = value.Trim();
            var displayName = ResolveHeaderDisplayName(httpContext)
                ?? ResolveDisplayName(httpContext.User)
                ?? principalId;

            // Opting in to header names declares trust in the forwarding infrastructure.
            return new SurfacePrincipalContext(principalId, displayName, true, $"header:{headerName}");
        }

        return null;
    }

    private string? ResolveHeaderDisplayName(HttpContext httpContext)
    {
        foreach (var headerName in options.PrincipalDisplayNameHeaderNames.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var value = httpContext.Request.Headers[headerName].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private async Task<SurfacePrincipalContext?> ResolveFromClaimsAsync(HttpContext? httpContext)
    {
        var user = await ResolveAuthenticationStateUserAsync().ConfigureAwait(false);
        if (user?.Identity?.IsAuthenticated != true)
        {
            user = httpContext?.User;
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        foreach (var claimType in options.PrincipalClaimTypes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var claim = user.FindFirst(claimType);
            if (!string.IsNullOrWhiteSpace(claim?.Value))
            {
                return new SurfacePrincipalContext(claim.Value, ResolveDisplayName(user), true, claim.Type);
            }
        }

        return null;
    }

    private async Task<ClaimsPrincipal?> ResolveAuthenticationStateUserAsync()
    {
        if (serviceProvider.GetService(typeof(AuthenticationStateProvider)) is not AuthenticationStateProvider provider)
        {
            return null;
        }

        try
        {
            var state = await provider.GetAuthenticationStateAsync().ConfigureAwait(false);
            return state.User;
        }
        catch
        {
            // ServerAuthenticationStateProvider throws when authentication state was never
            // set (host without auth, or resolution outside a render context). Fail closed
            // to the HttpContext user instead.
            return null;
        }
    }

    private static string? ResolveDisplayName(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.Name)?.Value
           ?? user?.FindFirst("name")?.Value
           ?? user?.FindFirst("preferred_username")?.Value
           ?? user?.Identity?.Name;

    private SurfacePrincipalContext Normalize(SurfacePrincipalContext principal)
    {
        if (!options.NormalizePrincipalIds || !principal.IsResolved)
        {
            return principal;
        }

        var normalized = SurfacePrincipalId.Normalize(principal.PrincipalId);
        return normalized == principal.PrincipalId ? principal : principal with { PrincipalId = normalized };
    }

    private SurfacePrincipalContext CacheAuthenticated(SurfacePrincipalContext principal)
    {
        // Fallback and unresolved results are never memoized so a late ambient set,
        // resolver, or newly available HttpContext can still win on a later call.
        if (principal.IsAuthenticated)
        {
            memoized = principal;
            SchedulePersistence(principal);
        }

        return principal;
    }

    private void SchedulePersistence(SurfacePrincipalContext principal)
    {
        pendingPersist = principal;

        if (persistenceRegistered
            || serviceProvider.GetService(typeof(PersistentComponentState)) is not PersistentComponentState state)
        {
            return;
        }

        try
        {
            // The render-mode filter is required in .NET 8+; an unfiltered callback throws
            // when the host persists for a specific interactive render mode.
            persistingSubscription = state.RegisterOnPersisting(() =>
            {
                state.PersistAsJson(PersistenceKey, pendingPersist);
                return Task.CompletedTask;
            }, RenderMode.InteractiveServer);
            persistenceRegistered = true;
        }
        catch
        {
            // Best effort: hosts without prerendering resolve again inside the circuit.
        }
    }

    private SurfacePrincipalContext? TakePersisted()
    {
        if (serviceProvider.GetService(typeof(PersistentComponentState)) is not PersistentComponentState state)
        {
            return null;
        }

        try
        {
            return state.TryTakeFromJson<SurfacePrincipalContext>(PersistenceKey, out var persisted) ? persisted : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
