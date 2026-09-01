using FabrCore.Host.A2A.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Publishes the host's agents over the open Agent2Agent (A2A) protocol, so Microsoft 365 Copilot
/// Studio — or any other A2A client — can add them as connected agents.
/// </summary>
/// <remarks>
/// <para>
/// Every FabrCore server has this. <c>AddFabrCoreServer</c> registers the services and
/// <c>UseFabrCoreServer</c> maps the routes, both gated on <c>A2A:Enabled</c>, so turning A2A on is
/// a configuration change and nothing else. Use <c>FabrCoreServerOptions.ConfigureA2A</c> for
/// code-level settings. <b>An ordinary host does not call the methods here.</b>
/// </para>
/// <para>
/// They are public for hosts that compose the pipeline themselves — chiefly tests, which want the
/// A2A routes without an Orleans silo. <c>FabrCore.Host.Testing</c> wraps exactly that. Both
/// methods are idempotent, so calling them alongside <c>AddFabrCoreServer</c> is safe.
/// </para>
/// </remarks>
public static class A2AExtensions
{
    /// <summary>
    /// Registers the A2A protocol services: the agent catalog resolved from configuration, the
    /// agent card factory, the task executor and store, caller authentication, and principal
    /// mapping. Registers nothing when <c>A2A:Enabled</c> is false.
    /// </summary>
    /// <remarks>
    /// <c>AddFabrCoreServer</c> calls this. Call it directly only when composing a host without
    /// <c>AddFabrCoreServer</c>. Register your own <see cref="IA2APrincipalResolver"/>,
    /// <see cref="IA2ATaskStore"/>, or other replacement <b>before</b> this runs — the host uses
    /// <c>TryAdd</c>, so a later registration is silently ignored.
    /// </remarks>
    /// <param name="builder">The host application builder (works with <c>WebApplicationBuilder</c>).</param>
    /// <param name="configure">Optional code-level override applied after configuration binding.</param>
    public static IHostApplicationBuilder AddA2A(
        this IHostApplicationBuilder builder,
        Action<A2AOptions>? configure = null)
    {
        // AddFabrCoreServices calls this, and a host may also have configured A2A itself.
        // Registering twice would double-map every route.
        if (builder.Services.Any(d => d.ServiceType == typeof(A2AMarker)))
        {
            return builder;
        }

        // The A2A section may live in fabrcore.json, which the FabrCore host does not load into
        // IConfiguration by itself. Pull it in when the section is not already present.
        if (!builder.Configuration.GetSection(A2ADefaults.SectionName).Exists())
        {
            builder.Configuration.AddJsonFile("fabrcore.json", optional: true, reloadOnChange: true);
        }

        var section = builder.Configuration.GetSection(A2ADefaults.SectionName);
        var options = section.Get<A2AOptions>() ?? new A2AOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(new A2AMarker(options.Enabled));

        if (!options.Enabled)
        {
            return builder;
        }

        Validate(options);

        // IOptionsMonitor is what the authentication handler reads, so bind the section as well
        // as supplying the snapshot above.
        builder.Services.Configure<A2AOptions>(section);
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IA2AAgentCatalog, A2AAgentCatalog>();
        builder.Services.TryAddSingleton<IA2AHarnessSkillResolver, A2AHarnessSkillResolver>();
        builder.Services.TryAddSingleton<IA2AAgentCardFactory, A2AAgentCardFactory>();
        builder.Services.TryAddSingleton<IA2AAgentProvisioner, A2AAgentProvisioner>();
        builder.Services.TryAddSingleton<IA2APrincipalResolver, DefaultA2APrincipalResolver>();
        builder.Services.TryAddSingleton<IA2ATaskStore, InMemoryA2ATaskStore>();
        builder.Services.TryAddSingleton<IA2ATaskExecutor, A2ATaskExecutor>();
        builder.Services.TryAddSingleton<A2ARequestHandler>();

        AddAuthentication(builder, options);

        return builder;
    }

    /// <summary>
    /// Maps the A2A endpoints for every exposed agent: the agent cards on both well-known routes,
    /// the JSON-RPC binding at the agent's base path, and the HTTP+JSON binding under
    /// <c>{base}/v1</c>. Maps nothing when <c>A2A:Enabled</c> is false.
    /// </summary>
    /// <remarks><c>UseFabrCoreServer</c> calls this. Safe to call twice; the second is a no-op.</remarks>
    public static WebApplication UseA2A(this WebApplication app)
    {
        var marker = app.Services.GetService<A2AMarker>()
            ?? throw new InvalidOperationException(
                "A2A services are not registered. AddFabrCoreServer registers them; a host that "
                + "bypasses it must call AddFabrCoreServices.");

        if (marker.Mapped)
        {
            return app;
        }

        marker.Mapped = true;

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FabrCore.Host.A2A");

        if (!marker.Enabled)
        {
            logger.LogInformation("FabrCore A2A addon is disabled (A2A:Enabled = false).");
            return app;
        }

        var options = app.Services.GetRequiredService<IOptions<A2AOptions>>().Value;
        var catalog = app.Services.GetRequiredService<IA2AAgentCatalog>();
        var prefix = A2AAgentCatalog.NormalizeRoutePrefix(options.RoutePrefix);

        // One parameterized route set serves every agent, so agents discovered from live cluster
        // state after startup are reachable without remapping or a restart.
        MapAgentRoutes(app, prefix, options);
        MapRootAgentCard(app, options);
        MapRoutePrefixAgentCard(app, prefix, options);

        if (options.EnableCatalogEndpoint)
        {
            MapCatalog(app, prefix, options);
        }

        var published = catalog.ListAsync().AsTask().GetAwaiter().GetResult();
        if (published.Count == 0 && options.Discovery.IncludeAgentHandles.Count == 0)
        {
            logger.LogWarning(
                "FabrCore A2A is enabled but publishes no agents. Set A2A:Discovery:AgentTypes to " +
                "'Described' to publish every registered agent type that has a [Description], or name " +
                "agents in A2A:AgentTypes / A2A:AgentHandles / A2A:Agents.");
            return app;
        }

        var baseUrl = options.PublicBaseUrl?.TrimEnd('/') ?? string.Empty;
        logger.LogInformation(
            "FabrCore A2A ready at {Prefix} with {Count} agent(s): {Agents}. Authentication: {Auth}. " +
            "Catalog at {Catalog}.",
            prefix,
            published.Count,
            string.Join(", ", published.Select(a => $"{a.Name} ({a.Source})")),
            options.Authentication.Mode,
            baseUrl + prefix);

        return app;
    }

    private static void MapAgentRoutes(WebApplication app, string prefix, A2AOptions options)
    {
        var requiresAuth = options.Authentication.Mode != A2AAuthenticationMode.None;
        var agentRoot = prefix + "/{agent}";

        // Agent cards. A client does not resolve the well-known path against the agent's base
        // path — it appends it to whatever URL it was configured with, and it does not agree with
        // other clients on what the file is called. Copilot Studio, configured with the message
        // endpoint, asks for {endpoint}/.well-known/{name} across five spellings and then falls
        // back to a bare GET on the endpoint itself. So the card is served under every base
        // segment a client may have been handed, under every spelling, plus on the endpoints
        // themselves. Cheap routes, and the difference between auto-filled metadata and a
        // "we couldn't find an agent card at this URL" the operator cannot act on.
        foreach (var segment in A2ADefaults.AgentCardBaseSegments)
        {
            foreach (var fileName in A2ADefaults.WellKnownAgentCardFileNames)
            {
                MapCard(agentRoot + segment + "/.well-known/" + fileName);
            }
        }

        MapCard(agentRoot + "/v1/card");

        // Last-resort probe: a bare GET on the configured endpoint. The message endpoints are
        // POST-only for real traffic, so answering GET with the card costs nothing and is the
        // only thing a client has left to try once the well-known paths have missed.
        MapCard(agentRoot);
        MapCard(agentRoot + "/v1/message:stream");
        MapCard(agentRoot + "/v1/message:send");

        void MapCard(string pattern)
        {
            var card = app.MapGet(
                pattern,
                (HttpContext context, A2ARequestHandler handler, string agent)
                    => handler.WriteAgentCardAsync(context, agent));

            if (requiresAuth && !options.Authentication.AllowAnonymousAgentCard)
            {
                card.RequireAuthorization(A2ADefaults.AuthorizationPolicy);
            }
            else
            {
                card.AllowAnonymous();
            }
        }

        // JSON-RPC binding: every method arrives as a POST to the agent's base path.
        Secure(app.MapPost(
            agentRoot,
            (HttpContext context, A2ARequestHandler handler, string agent)
                => handler.HandleJsonRpcAsync(context, agent)));

        // HTTP+JSON binding.
        Secure(app.MapPost(
            agentRoot + "/v1/message:send",
            (HttpContext context, A2ARequestHandler handler, string agent)
                => handler.HandleHttpMessageAsync(context, agent, streamingRoute: false)));

        Secure(app.MapPost(
            agentRoot + "/v1/message:stream",
            (HttpContext context, A2ARequestHandler handler, string agent)
                => handler.HandleHttpMessageAsync(context, agent, streamingRoute: true)));

        Secure(app.MapGet(
            agentRoot + "/v1/tasks/{taskId}",
            (HttpContext context, A2ARequestHandler handler, string agent, string taskId)
                => handler.HandleHttpGetTaskAsync(context, agent, taskId)));

        Secure(app.MapPost(
            agentRoot + "/v1/tasks/{taskId}:cancel",
            (HttpContext context, A2ARequestHandler handler, string agent, string taskId)
                => handler.HandleHttpCancelTaskAsync(context, agent, taskId)));

        Secure(app.MapPost(
            agentRoot + "/v1/tasks/{taskId}:subscribe",
            (HttpContext context, A2ARequestHandler handler, string agent, string taskId)
                => handler.HandleHttpSubscribeAsync(context, agent, taskId)));

        void Secure(RouteHandlerBuilder route)
        {
            if (requiresAuth)
            {
                route.RequireAuthorization(A2ADefaults.AuthorizationPolicy);
            }
            else
            {
                route.AllowAnonymous();
            }
        }
    }

    private static void MapRootAgentCard(WebApplication app, A2AOptions options)
    {
        // A client given only the server's host name looks here first, and probes the same set of
        // spellings it uses against an agent's own base path.
        foreach (var route in A2ADefaults.WellKnownAgentCardFileNames.Select(name => "/.well-known/" + name))
        {
            var card = app.MapGet(
                route,
                (HttpContext context, A2ARequestHandler handler) => handler.WritePrimaryAgentCardAsync(context));

            if (options.Authentication.Mode != A2AAuthenticationMode.None
                && !options.Authentication.AllowAnonymousAgentCard)
            {
                card.RequireAuthorization(A2ADefaults.AuthorizationPolicy);
            }
            else
            {
                card.AllowAnonymous();
            }
        }
    }

    private static void MapCatalog(WebApplication app, string prefix, A2AOptions options)
        => app.MapGet(
                prefix,
                (HttpContext context, A2ARequestHandler handler) => handler.WriteCatalogAsync(context))
            .AllowAnonymous();

    private static void MapRoutePrefixAgentCard(WebApplication app, string prefix, A2AOptions options)
    {
        // A client configured with just the route prefix (Copilot Studio's own tooltip suggests
        // "https://your-domain.com/a2a") appends the well-known path to it. Answer with the
        // primary agent's card, the same one the server root serves.
        foreach (var route in A2ADefaults.WellKnownAgentCardFileNames
                     .Select(name => prefix + "/.well-known/" + name))
        {
            var card = app.MapGet(
                route,
                (HttpContext context, A2ARequestHandler handler) => handler.WritePrimaryAgentCardAsync(context));

            if (options.Authentication.Mode != A2AAuthenticationMode.None
                && !options.Authentication.AllowAnonymousAgentCard)
            {
                card.RequireAuthorization(A2ADefaults.AuthorizationPolicy);
            }
            else
            {
                card.AllowAnonymous();
            }
        }
    }

    private static void AddAuthentication(IHostApplicationBuilder builder, A2AOptions options)
    {
        switch (options.Authentication.Mode)
        {
            case A2AAuthenticationMode.ApiKey:
                builder.Services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, A2AApiKeyAuthenticationHandler>(
                        A2ADefaults.ApiKeyScheme, _ => { });
                AddPolicy(builder, A2ADefaults.ApiKeyScheme);
                break;

            case A2AAuthenticationMode.JwtBearer:
                var jwt = options.Authentication.JwtBearer;
                builder.Services.AddAuthentication()
                    .AddJwtBearer(A2ADefaults.JwtBearerScheme, bearer =>
                    {
                        bearer.Authority = jwt.Authority;
                        bearer.Audience = jwt.Audience;
                        bearer.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
                        bearer.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = jwt.ValidIssuers.Count > 0 || !string.IsNullOrWhiteSpace(jwt.Authority),
                            ValidIssuers = jwt.ValidIssuers.Count > 0 ? jwt.ValidIssuers : null,
                            ValidateAudience = !string.IsNullOrWhiteSpace(jwt.Audience) || jwt.ValidAudiences.Count > 0,
                            ValidAudiences = jwt.ValidAudiences.Count > 0 ? jwt.ValidAudiences : null,
                            ValidateLifetime = true,
                        };
                    });
                AddPolicy(builder, A2ADefaults.JwtBearerScheme);
                break;

            case A2AAuthenticationMode.None:
            default:
                break;
        }

        static void AddPolicy(IHostApplicationBuilder builder, string scheme)
            => builder.Services.AddAuthorizationBuilder()
                .AddPolicy(A2ADefaults.AuthorizationPolicy, policy => policy
                    .AddAuthenticationSchemes(scheme)
                    .RequireAuthenticatedUser());
    }

    private static void Validate(A2AOptions options)
    {
        // Every agent is served under {RoutePrefix}/{agent}. An empty prefix would turn that into
        // a top-level "/{agent}" catch-all that shadows the rest of the application.
        if (A2AAgentCatalog.NormalizeRoutePrefix(options.RoutePrefix).Length == 0)
        {
            throw new InvalidOperationException(
                "A2A: RoutePrefix must be a non-empty path such as '/a2a'. Agents are served under " +
                "'{RoutePrefix}/{agent}', so an empty prefix would claim every top-level route on this server.");
        }

        if (options.Discovery.RefreshInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A2A: Discovery:RefreshInterval must be greater than zero.");
        }

        if (options.Principal.Prefix?.Contains(':') == true)
        {
            throw new InvalidOperationException(
                "A2A: Principal:Prefix must not contain ':' — it is the FabrCore handle separator.");
        }

        if (options.Authentication.Mode == A2AAuthenticationMode.ApiKey)
        {
            var keys = options.Authentication.ApiKey.Keys;
            if (keys.Count == 0 || keys.All(k => string.IsNullOrWhiteSpace(k.Value)))
            {
                throw new InvalidOperationException(
                    "A2A: Authentication:ApiKey:Keys must contain at least one key with a Value when " +
                    "Authentication:Mode is 'ApiKey'. Set Mode to 'None' only when a proxy in front of this " +
                    "server authenticates A2A callers.");
            }

            if (string.IsNullOrWhiteSpace(options.Authentication.ApiKey.HeaderName)
                && string.IsNullOrWhiteSpace(options.Authentication.ApiKey.QueryParameterName))
            {
                throw new InvalidOperationException(
                    "A2A: Authentication:ApiKey needs a HeaderName or a QueryParameterName for callers to present the key in.");
            }
        }

        if (options.Authentication.Mode == A2AAuthenticationMode.JwtBearer
            && string.IsNullOrWhiteSpace(options.Authentication.JwtBearer.Authority)
            && options.Authentication.JwtBearer.ValidIssuers.Count == 0)
        {
            throw new InvalidOperationException(
                "A2A: Authentication:JwtBearer:Authority (or ValidIssuers) is required when Authentication:Mode is 'JwtBearer'.");
        }

        if (options.Principal.Strategy == A2APrincipalStrategy.ApiKey
            && options.Authentication.Mode != A2AAuthenticationMode.ApiKey)
        {
            throw new InvalidOperationException(
                "A2A: Principal:Strategy 'ApiKey' requires Authentication:Mode 'ApiKey'.");
        }

        if (options.Principal.Strategy == A2APrincipalStrategy.Claim
            && options.Authentication.Mode != A2AAuthenticationMode.JwtBearer)
        {
            throw new InvalidOperationException(
                "A2A: Principal:Strategy 'Claim' requires Authentication:Mode 'JwtBearer'.");
        }

        foreach (var agent in options.Agents)
        {
            var label = agent.Name ?? agent.AgentType ?? agent.AgentHandle ?? "<unnamed>";

            if (!string.IsNullOrWhiteSpace(agent.AgentType) && !string.IsNullOrWhiteSpace(agent.AgentHandle))
            {
                throw new InvalidOperationException(
                    $"A2A: agent '{label}' sets both AgentType and AgentHandle. Use AgentType to provision an agent " +
                    "per caller, or AgentHandle to route to one that already exists.");
            }

            if (string.IsNullOrWhiteSpace(agent.AgentType) && string.IsNullOrWhiteSpace(agent.AgentHandle))
            {
                throw new InvalidOperationException(
                    $"A2A: agent '{label}' must set AgentType or AgentHandle.");
            }

            if (!string.IsNullOrWhiteSpace(agent.AgentHandle) && !agent.AgentHandle.Contains(':'))
            {
                throw new InvalidOperationException(
                    $"A2A: agent '{label}' has AgentHandle '{agent.AgentHandle}', which must be fully qualified " +
                    "(\"principalHandle:agentHandle\", for example \"system:assistant\").");
            }
        }

        foreach (var handle in options.AgentHandles.Where(h => !string.IsNullOrWhiteSpace(h) && !h.Contains(':')))
        {
            throw new InvalidOperationException(
                $"A2A: AgentHandles entry '{handle}' must be fully qualified (\"principalHandle:agentHandle\").");
        }
    }

    /// <summary>
    /// Registered by <see cref="AddA2A"/> so <see cref="UseA2A"/> can verify ordering and so
    /// neither runs twice when a host configures A2A on top of the automatic wiring.
    /// </summary>
    internal sealed class A2AMarker(bool enabled)
    {
        public bool Enabled { get; } = enabled;

        /// <summary>Set once the routes are mapped.</summary>
        public bool Mapped { get; set; }
    }
}
