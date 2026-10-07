using FabrCore.Core.Auditing;
using FabrCore.Core.CloudServer;
using FabrCore.Host.Security;
using FabrCore.Sdk;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static FabrCore.Core.CloudServer.IntegrationAdministration;
using Marker = FabrCore.Services.Microsoft365Copilot.Microsoft365CopilotExtensions.Microsoft365CopilotMarker;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Administration routes for the channel, under <c>/fabrcoreapi/admin/v1/integrations/microsoft365</c>.
/// They are mapped whether or not the channel is on, so a management console always gets an
/// answer it can render, and they require the host's administration policy.
/// </summary>
internal static class CopilotIntegrationEndpoints
{
    internal static readonly TimeSpan CredentialTimeout = TimeSpan.FromSeconds(20);
    private const string BotServiceResource = "https://api.botframework.com";
    private const int MaxMessageLength = 300;

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Microsoft365Route)
            .RequireAuthorization(FabrCoreAdminAuthenticationDefaults.Policy);
        group.AddEndpointFilter(async (context, next) =>
        {
            // These routes describe the host. A target principal here is a caller error, and
            // accepting one silently would look like it had been honored.
            var headers = context.HttpContext.Request.Headers;
            if (headers.ContainsKey("X-FabrCore-Admin-Target") || headers.ContainsKey("x-user") || headers.ContainsKey("x-user-handle"))
                return Results.BadRequest(new { error = "This route describes the host and accepts no target principal." });
            // Always regenerated from live options; a cached copy is how stale manifests get uploaded.
            context.HttpContext.Response.Headers.CacheControl = "no-store, no-cache";
            return await next(context);
        });

        group.MapGet("", (IServiceProvider services) => Results.Ok(BuildStatus(services)));

        group.MapGet("/" + ManifestSegment, (IServiceProvider services) =>
            Package(services, builder => Results.Text(builder.BuildManifestJson(), "application/json")));

        group.MapGet("/" + AppPackageSegment, (IServiceProvider services) =>
            Package(services, builder => Results.File(builder.BuildPackageZip(), "application/zip", "appPackage.zip")));

        group.MapPost("/" + DiagnosticsSegment, async (HttpContext http, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var report = await RunDiagnosticsAsync(services, cancellationToken);
            if (services.GetService<IAuditProvider>() is { } audit)
            {
                await audit.RecordAsync(new AuditEvent
                {
                    Category = AuditCategory.RemoteAdministration,
                    Outcome = AuditOutcome.Success,
                    SubjectPrincipal = http.Request.Headers["X-FabrCore-Admin-Actor"].FirstOrDefault() ?? http.User.Identity?.Name ?? "cluster-admin",
                    Resource = "integrations/" + Microsoft365 + "/" + DiagnosticsSegment,
                    Permission = "integrations.admin",
                    Details = new()
                    {
                        ["operation"] = "diagnostics",
                        ["status"] = report.Status,
                        ["commandId"] = http.Request.Headers["X-FabrCore-Admin-Command-Id"].FirstOrDefault() ?? string.Empty
                    }
                });
            }
            return Results.Ok(report);
        });
    }

    internal static Microsoft365IntegrationStatus BuildStatus(IServiceProvider services)
    {
        var marker = services.GetRequiredService<Marker>();
        var options = services.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value;
        // Neither service is registered while the channel is off.
        var storage = marker.Enabled ? services.GetService<IStorage>()?.GetType() : null;
        var customResolver = marker.Enabled && services.GetService<ICopilotPrincipalResolver>() is not null and not DefaultCopilotPrincipalResolver;
        return CopilotIntegrationStatusBuilder.Build(options, marker, storage, customResolver);
    }

    private static IResult Package(IServiceProvider services, Func<CopilotAppPackageBuilder, IResult> build)
    {
        var marker = services.GetRequiredService<Marker>();
        if (!marker.Enabled)
            return Conflict("channel-disabled", marker.DisabledReason ?? "The Microsoft 365 Copilot channel is off on this host.");
        try
        {
            return build(services.GetRequiredService<CopilotAppPackageBuilder>());
        }
        catch (InvalidOperationException ex)
        {
            // The channel is on but cannot describe itself yet, for example with no client id.
            return Conflict("manifest-unavailable", ex.Message);
        }
    }

    private static IResult Conflict(string error, string message) =>
        Results.Json(new { error, message }, statusCode: StatusCodes.Status409Conflict);

    internal static async Task<IntegrationDiagnosticsReport> RunDiagnosticsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var marker = services.GetRequiredService<Marker>();
        var options = services.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value;
        var checks = BuildStatus(services).Findings;
        checks.Add(AgentTypeCheck(options, marker, services.GetService<IFabrCoreRegistry>()));
        checks.Add(await BotServiceCredentialCheckAsync(options, marker, services.GetService<IConnections>(), cancellationToken));
        return new IntegrationDiagnosticsReport
        {
            Integration = Microsoft365,
            ObservedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            Status = Summarize(checks)
        };
    }

    internal static IntegrationCheck AgentTypeCheck(Microsoft365CopilotOptions options, Marker marker, IFabrCoreRegistry? registry)
    {
        const string id = "agent-type-registered";
        if (!marker.Enabled)
            return Check(id, Skipped, "The Microsoft 365 Copilot channel is off on this host.");
        if (!string.IsNullOrWhiteSpace(options.Agent.SharedAgentHandle))
            return Check(id, Skipped, $"Conversations route to the existing agent '{options.Agent.SharedAgentHandle}'; no agent type is provisioned.");
        if (registry is null)
            return Check(id, Skipped, "The agent registry is not available on this host.");
        return registry.FindAgentType(options.Agent.AgentType!) is not null
            ? Check(id, Pass, $"Agent type '{options.Agent.AgentType}' is registered.")
            : Check(id, Fail, $"Agent type '{options.Agent.AgentType}' is not registered on this host. Check its [AgentAlias] and that its assembly is loaded.");
    }

    private static Task<IntegrationCheck> BotServiceCredentialCheckAsync(
        Microsoft365CopilotOptions options, Marker marker, IConnections? connections, CancellationToken cancellationToken)
    {
        const string id = BotServiceCredentialCheckId;
        if (!marker.Enabled)
            return Task.FromResult(Check(id, Skipped, "The Microsoft 365 Copilot channel is off on this host."));
        if (connections is null)
            return Task.FromResult(Check(id, Skipped, "The Agents SDK connection service is not available on this host."));
        if (!marker.NativeServiceConnection && string.IsNullOrWhiteSpace(options.ClientId))
            return Task.FromResult(Check(id, Skipped, "No bot identity is configured, so there is no credential to test."));

        IAccessTokenProvider provider;
        try
        {
            provider = connections.GetConnection(Microsoft365CopilotDefaults.ServiceConnectionName);
        }
        catch (Exception) when (marker.NativeServiceConnection)
        {
            return Task.FromResult(Check(id, Skipped,
                $"The host configures the Agents SDK Connections section itself and has no connection named '{Microsoft365CopilotDefaults.ServiceConnectionName}'."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Check(id, Fail, Describe(ex)));
        }

        // forceRefresh: a cached token would pass after the credential behind it stopped working.
        return CredentialCheckAsync(
            () => provider.GetAccessTokenAsync(BotServiceResource, [BotServiceResource + "/.default"], forceRefresh: true),
            CredentialTimeout, cancellationToken);
    }

    internal const string BotServiceCredentialCheckId = "bot-service-credential";

    /// <summary>
    /// Proves the host can authenticate as the bot. The token is never returned, logged, or kept:
    /// only whether one arrived. A failure reports the exception type and the first line of its
    /// message, because identity provider errors can run to a page and can echo request details.
    /// </summary>
    internal static async Task<IntegrationCheck> CredentialCheckAsync(
        Func<Task<string>> acquire, TimeSpan timeout, CancellationToken cancellationToken)
    {
        const string id = BotServiceCredentialCheckId;
        try
        {
            var acquired = !string.IsNullOrEmpty(await acquire().WaitAsync(timeout, cancellationToken));
            return acquired
                ? Check(id, Pass, "The host acquired a Bot Service token with the configured credential. The token was discarded.")
                : Check(id, Fail, "The identity provider answered without a token.");
        }
        catch (TimeoutException)
        {
            return Check(id, Fail, $"No Bot Service token was acquired within {timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Check(id, Fail, Describe(ex));
        }
    }

    private static string Describe(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        var end = message.IndexOfAny(['\r', '\n']);
        if (end >= 0) message = message[..end];
        message = message.Trim();
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];
        return message.Length == 0 ? exception.GetType().Name : $"{exception.GetType().Name}: {message}";
    }

    private static IntegrationCheck Check(string id, string status, string message) => new() { Id = id, Status = status, Message = message };
}
