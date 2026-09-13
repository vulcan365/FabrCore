using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace FabrCore.Services.Connections;

public sealed class ConnectionsOptions
{
    public bool Enabled { get; set; }
    public bool EntraAgentIdEnabled { get; set; }
    public bool ClientHandoffEnabled { get; set; }
    public string? HandoffAuthority { get; set; }
    public string? HandoffAudience { get; set; }
    /// <summary>Legacy compatibility flag. Host now selects and validates actual protection; this flag is not required or trusted.</summary>
    public bool ProtectedKeyRingConfigured { get; set; }
}

public interface IConnectionPrincipalResolver
{
    string? Resolve(ClaimsPrincipal principal);
}

internal sealed class EntraConnectionPrincipalResolver : IConnectionPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var tid = principal.FindFirst("tid")?.Value ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        var oid = principal.FindFirst("oid")?.Value ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        return Guid.TryParse(tid, out _) && Guid.TryParse(oid, out _) ? EntraPrincipalHandle.Create(tid!, oid!) : null;
    }
}

public static class ConnectionsExtensions
{
    /// <summary>Explicit opt-in. Host supplies protection defaults; the application supplies user authentication.</summary>
    public static IServiceCollection AddFabrCoreConnections(this IServiceCollection services, Action<ConnectionsOptions> configure)
    {
        var options = new ConnectionsOptions(); configure(options);
        if (!options.Enabled) return services;
        services.AddSingleton(options);
        services.AddSingleton(new ConnectionCapabilities(options.EntraAgentIdEnabled, options.ClientHandoffEnabled));
        services.AddFabrCoreDataProtection();
        services.AddHostedService<ConnectionProtectionStartup>();
        services.AddEndpointsApiExplorer();
        services.TryAddSingleton<IConnectionPrincipalResolver, EntraConnectionPrincipalResolver>();
        services.TryAddSingleton<IConnectionCredentialProvider, ConfigurationCredentialProvider>();
        services.TryAddSingleton<IConnectionHandoffPrincipalValidator, ConnectionHandoffPrincipalValidator>();
        services.AddSingleton(sp => new OAuthProvider(sp.GetRequiredService<IConnectionCredentialProvider>()));
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<IConnectionService>(sp => sp.GetRequiredService<ConnectionService>());
        services.AddSingleton<FabrCore.Core.Blueprints.IBlueprintExpander, ConnectionBlueprintExpander>();
        return services;
    }

    public static IEndpointRouteBuilder MapFabrCoreConnections(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<ConnectionsOptions>()?.Enabled != true) return endpoints;
        var group = endpoints.MapGroup("/fabrcoreapi/connections/v1").RequireAuthorization();
        group.MapGet("", (HttpContext c, ConnectionService s, IConnectionPrincipalResolver r) => Run(c, r, p => s.Execute(p, "list", new(""))));
        group.MapPost("/begin", (BeginConnectionRequest b, HttpContext c, ConnectionService s, IConnectionPrincipalResolver r) =>
            Run(c, r, p => s.Execute(p, "begin", new(b.Connection, RedirectUri: b.RedirectUri))));
        group.MapPost("/complete", (CompleteConnectionRequest b, HttpContext c, ConnectionService s, IConnectionPrincipalResolver r) =>
            Run(c, r, p => s.Execute(p, "complete", new(b.Connection, State: b.State, Code: b.ProviderError is null ? b.AuthorizationCode : null))));
        group.MapPost("/assertion", (ConnectionAssertionRequest b, HttpContext c, ConnectionService s, IConnectionPrincipalResolver r) =>
            Run(c, r, p => s.Execute(p, "assertion", new(b.Connection, Assertion: b.Assertion))));
        group.MapDelete("/{name}", (string name, HttpContext c, ConnectionService s, IConnectionPrincipalResolver r) =>
            Run(c, r, p => s.Execute(p, "disconnect", new(name))));

        var admin = endpoints.MapGroup("/fabrcoreapi/admin/v1/principals/{principal}/connections").RequireAuthorization(FabrCoreAdminAuthenticationDefaults.Policy);
        admin.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            if (http.Request.Headers.ContainsKey("X-FabrCore-Admin-Target") || http.Request.Headers.ContainsKey("x-user") || http.Request.Headers.ContainsKey("x-user-handle"))
                return Results.BadRequest(new { error = "invalid-target", message = "Supply the principal only in the route." });
            var result = await next(context);
            if (http.Request.Method != "GET")
            {
                var audit = http.RequestServices.GetService<FabrCore.Core.Auditing.IAuditProvider>();
                if (audit is not null) await audit.RecordAsync(new() {
                    Category = FabrCore.Core.Auditing.AuditCategory.RemoteAdministration,
                    Outcome = result is IStatusCodeHttpResult { StatusCode: >= 400 } ? FabrCore.Core.Auditing.AuditOutcome.Denied : FabrCore.Core.Auditing.AuditOutcome.Success,
                    SubjectPrincipal = http.Request.Headers["X-FabrCore-Admin-Actor"].FirstOrDefault() ?? http.User.Identity?.Name ?? "cluster-admin",
                    ResourcePrincipal = http.Request.RouteValues["principal"]?.ToString(),
                    Resource = "connections/" + http.Request.RouteValues["name"], Permission = "connections.admin",
                    Details = new() { ["method"] = http.Request.Method, ["commandId"] = http.Request.Headers["X-FabrCore-Admin-Command-Id"].FirstOrDefault() ?? "" }
                });
            }
            return result;
        });
        admin.MapGet("", (string principal, ConnectionService s) => Result(() => s.Execute(principal, "list", new(""))));
        admin.MapGet("/{name}", (string principal, string name, ConnectionService s) => Result(() => s.Execute(principal, "profile", new(name))));
        admin.MapPut("/{name}", (string principal, string name, ConnectionProfile b, [Microsoft.AspNetCore.Mvc.FromHeader(Name = "If-Match")] string? revision, ConnectionService s) =>
            Result(() => s.Execute(principal, "save", new(name, Revision: revision?.Trim('"'), Profile: b))));
        admin.MapDelete("/{name}/authorization", (string principal, string name, ConnectionService s) => Result(() => s.Execute(principal, "disconnect", new(name))));
        admin.MapPost("/{name}/handoff", (string principal, string name, ConnectionService s) => Result(() => s.Execute(principal, "handoff-create", new(name))));
        admin.MapPost("/{name}/handoff/complete", (string principal, string name, ConnectionHandoffEnvelope envelope, ConnectionService s) =>
            Result(() => s.Execute(principal, "handoff-complete", new(name, Handoff: envelope))));
        return endpoints;
    }
    private static async Task<IResult> Run(HttpContext context, IConnectionPrincipalResolver resolver, Func<string, Task<string>> action)
    {
        // Cookie clients must send same-origin requests; bearer clients have no ambient credential.
        if (context.Request.Method != "GET" && context.Request.Cookies.Count > 0)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin != $"{context.Request.Scheme}://{context.Request.Host}") return Results.Forbid();
        }
        var principal = resolver.Resolve(context.User);
        if (principal is null) return Results.Unauthorized();
        var result = await Result(() => action(principal));
        if (context.Request.Method != "GET" && context.RequestServices.GetService<FabrCore.Core.Auditing.IAuditProvider>() is { } audit)
            await audit.RecordAsync(new() {
                Category = FabrCore.Core.Auditing.AuditCategory.AclDecision,
                Outcome = result is IStatusCodeHttpResult { StatusCode: >= 400 } ? FabrCore.Core.Auditing.AuditOutcome.Denied : FabrCore.Core.Auditing.AuditOutcome.Success,
                SubjectPrincipal = principal, ResourcePrincipal = principal, Resource = "connections", Permission = "connections.user",
                Details = new() { ["operation"] = context.Request.Method + " " + context.Request.Path }
            });
        return result;
    }
    private static async Task<IResult> Result(Func<Task<string>> action)
    {
        try { return Results.Content(await action(), "application/json"); }
        catch (ConnectionException e) { return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.Code switch {
            "not-found" => 404, "access-denied" => 403, "revision-conflict" => 412, "interaction-required" => 409, "feature-disabled" => 409,
            "provider-unavailable" => 503, "provider-timeout" => 504, "too-many-transactions" => 429, _ => 400 }); }
    }
}

internal sealed class ConnectionService(IClusterClient cluster) : IConnectionService
{
    public async Task<string> GetSessionVersionAsync(string principal, string agent, ConnectionBinding binding, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<string>(await Execute(binding.OwnerPrincipal, "session", new(binding.Name, principal, agent)).WaitAsync(cancellationToken))!;
    internal async Task<string> Execute(string owner, string operation, GrainRequest request)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ConnectionException("access-denied", "A stable principal is required.");
        var result = await cluster.GetGrain<IConnectionGrain>(owner).Execute(operation, JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        using var json = JsonDocument.Parse(result);
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("connectionError", out var error))
            throw new ConnectionException(error.GetString()!, json.RootElement.GetProperty("message").GetString()!);
        return result;
    }
    public async Task<ConnectionAccessToken> GetAccessTokenAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
    {
        var json = await Execute(binding.OwnerPrincipal, "token", new(binding.Name, principal, agent, resource)).WaitAsync(cancellationToken);
        var token = JsonSerializer.Deserialize<TokenMaterial>(json, JsonSerializerOptions.Web)!;
        return new(token.AccessToken, token.ExpiresAt);
    }
    public async Task<HttpClient> GetHttpClientAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
    {
        var json = await Execute(binding.OwnerPrincipal, "resource", new(binding.Name, principal, agent, resource)).WaitAsync(cancellationToken);
        var config = JsonSerializer.Deserialize<AuthorizedResource>(json, JsonSerializerOptions.Web)!;
        var baseUri = new Uri(config.Resource.BaseUrl);
        return new HttpClient(new ConnectionHandler(baseUri, async ct => {
            var material = JsonSerializer.Deserialize<TokenMaterial>(await Execute(binding.OwnerPrincipal, "token",
                new(binding.Name, principal, agent, resource, SessionVersion: config.SessionVersion)).WaitAsync(ct), JsonSerializerOptions.Web)!;
            return new ConnectionAccessToken(material.AccessToken, material.ExpiresAt);
        })) { BaseAddress = baseUri };
    }
}

internal sealed class ConnectionHandler(Uri baseUri, Func<CancellationToken, Task<ConnectionAccessToken>> tokenProvider)
    : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !baseUri.IsBaseOf(request.RequestUri))
            throw new ConnectionException("access-denied", "The request destination is outside the configured resource.");
        var token = await tokenProvider(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
