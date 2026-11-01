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

        // One package per agent: Microsoft allows a single bot in an app manifest.
        group.MapGet($"/{AgentsSegment}/{{name}}/{ManifestSegment}", (string name, IServiceProvider services) =>
            Package(services, name, (builder, agent) => Results.Text(builder.BuildManifestJson(agent), "application/json")));

        group.MapGet($"/{AgentsSegment}/{{name}}/{AppPackageSegment}", (string name, IServiceProvider services) =>
            Package(services, name, (builder, agent) => Results.File(builder.BuildPackageZip(agent), "application/zip", "appPackage.zip")));

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
        var agents = services.GetRequiredService<CopilotAgentCatalog>().Agents;
        return CopilotIntegrationStatusBuilder.Build(options, agents, marker, storage, customResolver);
    }

    private static IResult Package(IServiceProvider services, string name, Func<CopilotAppPackageBuilder, CopilotAgent, IResult> build)
    {
        var marker = services.GetRequiredService<Marker>();
        if (!marker.Enabled)
            return Conflict("channel-disabled", marker.DisabledReason ?? "The Microsoft 365 Copilot channel is off on this host.");
        var agent = services.GetRequiredService<CopilotAgentCatalog>().Find(name);
        if (agent is null)
            return Results.Json(new { error = "agent-not-found", message = $"This host exposes no Microsoft 365 Copilot agent named '{name}'." },
                statusCode: StatusCodes.Status404NotFound);
        try
        {
            return build(services.GetRequiredService<CopilotAppPackageBuilder>(), agent);
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
        var agents = services.GetRequiredService<CopilotAgentCatalog>().Agents;
        var checks = BuildStatus(services).Findings;
        checks.Add(AgentTypeCheck(agents, marker, services.GetService<IFabrCoreRegistry>()));
        checks.Add(await BotServiceCredentialCheckAsync(agents, marker, services.GetService<IConnections>(), cancellationToken));
        return new IntegrationDiagnosticsReport
        {
            Integration = Microsoft365,
            ObservedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            Status = Summarize(checks)
        };
    }

    internal static IntegrationCheck AgentTypeCheck(IReadOnlyList<CopilotAgent> agents, Marker marker, IFabrCoreRegistry? registry)
    {
        const string id = "agent-type-registered";
        if (!marker.Enabled)
            return Check(id, Skipped, "The Microsoft 365 Copilot channel is off on this host.");
        if (agents.Count == 0)
            return Check(id, Skipped, "No agent is configured in Microsoft365Copilot:Agents.");
        if (registry is null)
            return Check(id, Skipped, "The agent registry is not available on this host.");
        var missing = agents
            .Where(agent => registry.FindAgentType(agent.AgentType) is null)
            .Select(agent => $"'{agent.AgentType}' ({agent.Name})")
            .ToList();
        return missing.Count == 0
            ? Check(id, Pass, agents.Count == 1
                ? $"Agent type '{agents[0].AgentType}' is registered."
                : $"All {agents.Count} agent types are registered.")
            : Check(id, Fail, $"Not registered on this host: {string.Join(", ", missing)}. Check each [AgentAlias] and that its assembly is loaded.");
    }

    /// <summary>
    /// Tests every bot's credential and reports the first that fails, naming the agent, so one
    /// check id keeps covering the whole channel however many agents it exposes.
    /// </summary>
    private static async Task<IntegrationCheck> BotServiceCredentialCheckAsync(
        IReadOnlyList<CopilotAgent> agents, Marker marker, IConnections? connections, CancellationToken cancellationToken)
    {
        const string id = BotServiceCredentialCheckId;
        if (!marker.Enabled)
            return Check(id, Skipped, "The Microsoft 365 Copilot channel is off on this host.");
        if (connections is null)
            return Check(id, Skipped, "The Agents SDK connection service is not available on this host.");
        var identified = agents.Where(agent => !string.IsNullOrWhiteSpace(agent.ClientId)).ToList();
        if (identified.Count == 0)
            return Check(id, Skipped, "No bot identity is configured, so there is no credential to test.");

        var tested = 0;
        foreach (var agent in identified)
        {
            IAccessTokenProvider provider;
            try
            {
                provider = connections.GetConnection(agent.ConnectionName);
            }
            catch (Exception) when (marker.NativeServiceConnection)
            {
                // The host named its connections itself; there is nothing of ours to look up.
                continue;
            }
            catch (Exception ex)
            {
                return Check(id, Fail, $"Agent '{agent.Name}': {Describe(ex)}");
            }

            // forceRefresh: a cached token would pass after the credential behind it stopped working.
            var result = await CredentialCheckAsync(
                () => provider.GetAccessTokenAsync(BotServiceResource, [BotServiceResource + "/.default"], forceRefresh: true),
                CredentialTimeout, cancellationToken);
            if (result.Status != Pass)
                return Check(id, result.Status, $"Agent '{agent.Name}': {result.Message}");
            tested++;
        }

        if (tested == 0)
            return Check(id, Skipped, "The host configures the Agents SDK Connections section itself and names its connections differently, so there is no credential to test here.");
        return tested == 1
            ? Check(id, Pass, "The host acquired a Bot Service token with the configured credential. The token was discarded.")
            : Check(id, Pass, $"The host acquired a Bot Service token for each of {tested} agents. The tokens were discarded.");
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
