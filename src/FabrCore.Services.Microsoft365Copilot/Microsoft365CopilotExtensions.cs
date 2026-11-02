using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FabrCore.Host;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// FabrCore server addon that exposes the host's agents to Microsoft 365 Copilot and Teams.
/// Call <see cref="AddMicrosoft365Copilot"/> after <c>AddFabrCoreServer</c> and
/// <see cref="UseMicrosoft365Copilot"/> after <c>UseFabrCoreServer</c>:
/// <code>
/// builder.AddFabrCoreServer();
/// builder.AddMicrosoft365Copilot();
///
/// var app = builder.Build();
/// app.UseFabrCoreServer();
/// app.UseMicrosoft365Copilot();
/// app.Run();
/// </code>
/// Configuration comes from the <c>Microsoft365Copilot</c> section of fabrcore.json or
/// appsettings.json; its <c>Agents</c> list names the agents to expose, each on its own route with
/// its own Azure Bot registration. No changes to FabrCore.Host are required.
/// </summary>
public static class Microsoft365CopilotExtensions
{
    /// <summary>
    /// Registers the Microsoft 365 Copilot channel services: the Agents SDK adapter and
    /// <see cref="FabrCoreCopilotAgent"/> bridge, inbound Azure Bot Service token validation,
    /// Entra user-authorization (when handlers are configured), the principal mapper, the agent
    /// provisioner, and the app-package generator.
    /// </summary>
    /// <param name="builder">The host application builder (works with <c>WebApplicationBuilder</c>).</param>
    /// <param name="configure">Optional code-level override applied after configuration binding.</param>
    public static IHostApplicationBuilder AddMicrosoft365Copilot(
        this IHostApplicationBuilder builder,
        Action<Microsoft365CopilotOptions>? configure = null)
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(Microsoft365CopilotMarker)))
            return builder;
        // The Microsoft365Copilot section may live in fabrcore.json, which the FabrCore host does
        // not load into IConfiguration by itself. Pull it in when the section is not already
        // present from appsettings.json (or a host-added fabrcore.json).
        if (!builder.Configuration.GetSection(Microsoft365CopilotDefaults.SectionName).Exists())
        {
            builder.Configuration.AddJsonFile("fabrcore.json", optional: true, reloadOnChange: true);
        }

        var section = builder.Configuration.GetSection(Microsoft365CopilotDefaults.SectionName);
        var configured = section.Exists() || configure is not null;
        var options = section.Get<Microsoft365CopilotOptions>() ?? new Microsoft365CopilotOptions();
        configure?.Invoke(options);
        options.UserAuthorizationConfigured = section.GetSection("UserAuthorization:Handlers").Exists()
            || builder.Configuration.GetSection("AgentApplication:UserAuthorization:Handlers").Exists()
            || options.Agents.Any(agent => !string.IsNullOrWhiteSpace(agent.SignInConnectionName));

        string? disabledReason = null;
        if (!configured)
        {
            // Installed but untouched. Starting with the channel off lets one host image be
            // enabled later by configuration alone.
            options.Enabled = false;
            disabledReason = $"No {Microsoft365CopilotDefaults.SectionName} configuration was found and no options were supplied in code.";
        }
        else if (!options.Enabled)
        {
            disabledReason = $"{Microsoft365CopilotDefaults.SectionName}:Enabled is false.";
        }

        // Read before the configuration bridge runs: afterwards the section always exists.
        var nativeServiceConnection = builder.Configuration.GetSection("Connections").Exists();

        var ignoredLegacyKeys = options.Enabled ? CheckLegacyConfiguration(section) : [];

        // Resolved once: every channel setting needs a restart to change. Nothing is exposed
        // while the channel is off, so nothing is validated either.
        var agents = options.Enabled ? CopilotAgentCatalog.Resolve(options) : [];

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(new CopilotAgentCatalog(agents));
        builder.Services.AddSingleton(new Microsoft365CopilotMarker(options.Enabled, configured, disabledReason, nativeServiceConnection)
        {
            IgnoredLegacyKeys = ignoredLegacyKeys
        });

        // Registered even while the channel is off, so a management console can tell
        // "installed but disabled" from "not installed" and can see why it is off.
        builder.Services.AddSingleton<FabrCore.Services.Contracts.Capabilities.IFabrCoreCapabilityContributor, CopilotCapabilityContributor>();
        builder.Services.AddSingleton<FabrCore.Host.Configuration.Cloud.IFabrCoreRuntimeSettingsContributor, CopilotRuntimeSettingsContributor>();

        if (!options.Enabled)
        {
            return builder;
        }

        Validate(options, agents, builder.Configuration);

        // Feed the Microsoft 365 Agents SDK the configuration shape it expects
        // (Connections / ConnectionsMap / AgentApplication) from our single section.
        AgentsSdkConfigurationBridge.Inject(builder.Configuration, agents, section);

        builder.Services.AddHttpClient();

        // Turn/sign-in state store for the Agents SDK. In-memory by default; hosts that scale
        // out or use SSO in production should register a durable IStorage before this call.
        builder.Services.TryAddSingleton<IStorage, MemoryStorage>();

        builder.Services.TryAddSingleton<ICopilotPrincipalResolver, DefaultCopilotPrincipalResolver>();
        builder.Services.TryAddSingleton<ICopilotAgentProvisioner, CopilotAgentProvisioner>();
        builder.Services.TryAddSingleton<ICopilotConversationContextWriter, CopilotConversationContextWriter>();
        builder.Services.TryAddSingleton<ICopilotUserAssertionSink, ConnectionsCopilotUserAssertionSink>();
        builder.Services.AddSingleton<CopilotAppPackageBuilder>();

        if (options.Proactive.Enabled)
        {
            builder.Services.TryAddSingleton<ICopilotProactiveActivitySender, CopilotProactiveActivitySender>();
            builder.Services.TryAddSingleton<CopilotProactiveMessenger>();
            builder.Services.AddPrincipalMessageRelay<CopilotPrincipalMessageRelay>();
        }

        // Registers the CloudAdapter (IAgentHttpAdapter), IConnections, channel client factory,
        // background activity queue, and the bridge agent itself.
        builder.AddAgent<FabrCoreCopilotAgent>();

        if (options.TokenValidation.Enabled)
        {
            builder.Services.AddCopilotChannelAuthentication(options, agents);
        }

        return builder;
    }

    /// <summary>
    /// Maps one Azure Bot Service messaging endpoint per configured agent (default
    /// <c>/api/messages/{name}</c>) and, in development (or when explicitly enabled), the
    /// app-package download endpoints under <c>/m365copilot/{agent}</c> plus the name-addressed
    /// manifest at <c>/manifests/{name}.json</c>.
    /// The administration routes under <c>/fabrcoreapi/admin/v1/integrations/microsoft365</c> are
    /// mapped whether or not the channel is on, and require the host's administration policy.
    /// </summary>
    public static WebApplication UseMicrosoft365Copilot(this WebApplication app)
    {
        const string mappedKey = "FabrCore.Microsoft365Copilot.RoutesMapped";
        if (((Microsoft.AspNetCore.Builder.IApplicationBuilder)app).Properties.ContainsKey(mappedKey))
            return app;
        var marker = app.Services.GetService<Microsoft365CopilotMarker>()
            ?? throw new InvalidOperationException(
                "UseMicrosoft365Copilot requires AddMicrosoft365Copilot to be called on the builder first.");

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FabrCore.Services.Microsoft365Copilot");

        // The administration routes are mapped whether or not the channel is on, so a management
        // console always gets an answer it can render.
        ((Microsoft.AspNetCore.Builder.IApplicationBuilder)app).Properties[mappedKey] = true;
        CopilotIntegrationEndpoints.Map(app);

        if (!marker.Enabled)
        {
            logger.LogInformation("Microsoft 365 Copilot channel is off: {Reason}", marker.DisabledReason);
            return app;
        }

        var options = app.Services.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value;

        var catalog = app.Services.GetRequiredService<CopilotAgentCatalog>();

        if (marker.IgnoredLegacyKeys.Count > 0)
        {
            logger.LogWarning(
                "Microsoft365Copilot: {Keys} are single-agent settings this add-on no longer reads. " +
                "They are ignored because Microsoft365Copilot:Agents is present; remove them once no older host shares this configuration.",
                string.Join(", ", marker.IgnoredLegacyKeys));
        }

        if (catalog.Agents.Count == 0)
        {
            logger.LogWarning(
                "Microsoft 365 Copilot channel is on but {Section}:Agents is empty, so no messaging endpoint is mapped.",
                Microsoft365CopilotDefaults.SectionName);
            return app;
        }

        foreach (var agent in catalog.Agents)
        {
            // The bridge is resolved by its own type: a host may register an IAgent of its own.
            var messages = app.MapPost(
                agent.Route,
                (HttpContext context, IAgentHttpAdapter adapter, FabrCoreCopilotAgent bridge, CancellationToken cancellationToken) =>
                {
                    if (!CopilotAgentRouting.TryBind(context, agent, options))
                    {
                        logger.LogWarning(
                            "Rejected an activity on {Endpoint}: its token was not issued for the bot of agent {Agent}.",
                            agent.Route, agent.Name);
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    return adapter.ProcessAsync(context.Request, context.Response, bridge, cancellationToken);
                });

            if (options.TokenValidation.Enabled)
            {
                messages.RequireAuthorization(Microsoft365CopilotDefaults.AuthorizationPolicy);
            }
            else
            {
                messages.AllowAnonymous();
                logger.LogWarning(
                    "Microsoft 365 Copilot messages endpoint {Endpoint} accepts ANONYMOUS requests " +
                    "(Microsoft365Copilot:TokenValidation:Enabled = false). Local development only.",
                    agent.Route);
            }

            logger.LogInformation(
                "Microsoft 365 Copilot agent {Agent} ready: endpoint {Endpoint}, bot {ClientId}, agent type {AgentType} (per user, handle '{Handle}').",
                agent.Name, agent.Route, agent.ClientId ?? "(none)", agent.AgentType, agent.Handle);
        }

        if (options.UserAuthorizationConfigured && catalog.Agents.Count > 1 && catalog.Agents.All(agent => agent.SignInConnectionName is null))
        {
            logger.LogWarning(
                "Microsoft 365 Copilot user authorization is configured once and shared by all {Count} agents. " +
                "Its Azure Bot OAuth connection belongs to one bot registration, so sign-in works only for the agent that owns it.",
                catalog.Agents.Count);
        }

        if (options.Manifest.EnableAppPackageEndpoint ?? app.Environment.IsDevelopment())
        {
            MapAppPackageEndpoints(app);
            logger.LogInformation(
                "Microsoft 365 app packages available at {PackageRoute} (manifests at {ManifestRoute} and {NamedManifestRoute}).",
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/{agent}/appPackage.zip",
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/{agent}/manifest.json",
                Microsoft365CopilotDefaults.ManifestsRoutePrefix + "/{name}.json");
        }

        logger.LogInformation(
            "Microsoft 365 Copilot channel ready: {Count} agent(s), token validation {TokenValidation}, user SSO {Sso}.",
            catalog.Agents.Count,
            options.TokenValidation.Enabled ? "on" : "OFF",
            options.UserAuthorizationConfigured ? "on" : "off");

        return app;
    }

    private static void MapAppPackageEndpoints(WebApplication app)
    {
        app.MapGet(
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/{agent}/manifest.json",
                (string agent, HttpContext context, CopilotAgentCatalog catalog, CopilotAppPackageBuilder packageBuilder)
                    => catalog.Find(agent) is { } found
                        ? BuildPackageResult(context, () => Results.Text(packageBuilder.BuildManifestJson(found), "application/json"))
                        : Results.NotFound())
            .AllowAnonymous();

        app.MapGet(
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/{agent}/appPackage.zip",
                (string agent, HttpContext context, CopilotAgentCatalog catalog, CopilotAppPackageBuilder packageBuilder)
                    => catalog.Find(agent) is { } found
                        ? BuildPackageResult(context, () => Results.File(packageBuilder.BuildPackageZip(found), "application/zip", "appPackage.zip"))
                        : Results.NotFound())
            .AllowAnonymous();

        // Name-addressed manifest for tooling that fetches manifests by app name
        // (or app id): /manifests/my-fabrcore-agent.json.
        app.MapGet(
                Microsoft365CopilotDefaults.ManifestsRoutePrefix + "/{name}.json",
                (string name, HttpContext context, CopilotAppPackageBuilder packageBuilder)
                    => packageBuilder.FindByManifestName(name) is { } found
                        ? BuildPackageResult(context, () => Results.Text(packageBuilder.BuildManifestJson(found), "application/json"))
                        : Results.NotFound())
            .AllowAnonymous();

        static IResult BuildPackageResult(HttpContext context, Func<IResult> build)
        {
            // Always regenerated from live configuration — a cached copy is exactly the
            // stale-manifest confusion we want to avoid.
            context.Response.Headers.CacheControl = "no-store, no-cache";
            try
            {
                return build();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }
    }

    /// <summary>
    /// Looks for the single-agent settings this add-on used to read. On their own they are
    /// refused rather than ignored: a host that quietly dropped its agent's plugins and tools
    /// would still answer, wrongly. Beside an <c>Agents</c> list they are tolerated and reported,
    /// so one published configuration can serve old and new hosts while a cluster is upgraded.
    /// </summary>
    private static IReadOnlyList<string> CheckLegacyConfiguration(IConfigurationSection section)
    {
        string[] legacy = ["Agent", "MessagesEndpoint", "ClientId", "ClientSecret"];
        var found = legacy.Where(key => section.GetSection(key).Exists()).ToList();
        if (found.Count == 0 || section.GetSection("Agents").Exists())
        {
            return found;
        }

        throw new InvalidOperationException(
            $"Microsoft365Copilot: {string.Join(", ", found.Select(key => "'" + key + "'"))} " +
            "is no longer read. Agents are now listed in Microsoft365Copilot:Agents, one entry per Azure Bot " +
            "registration: { \"Name\", \"Route\", \"ClientId\", \"ClientSecret\", \"AgentType\", \"Handle\" }. " +
            "Plugins, tools, system prompt, and args are no longer set here; define them on the agent type or in the " +
            "blueprint that provisions the agent. SharedAgentHandle, AgentPerConversation, and Agent:Binding are gone.");
    }

    private static void Validate(
        Microsoft365CopilotOptions options, IReadOnlyList<CopilotAgent> agents, IConfiguration configuration)
    {
        if (options.Principal.Prefix?.Contains(':') == true)
        {
            throw new InvalidOperationException(
                "Microsoft365Copilot: Principal:Prefix must not contain ':' — it is the FabrCore handle separator.");
        }

        if (options.Proactive.Enabled)
        {
            if (options.Proactive.MaxDeliveryAttempts <= 0 ||
                options.Proactive.RetryBaseDelay <= TimeSpan.Zero ||
                options.Proactive.SendTimeout <= TimeSpan.Zero ||
                options.Proactive.WorkerShards <= 0 ||
                options.Proactive.OutboundQueueCapacity <= 0 ||
                options.Proactive.MaxStoredEndpoints <= 0)
            {
                throw new InvalidOperationException(
                    "Microsoft365Copilot:Proactive numeric counts and durations must be greater than zero.");
            }

            if (options.Proactive.AllowedConversationTypes.Count == 0 ||
                options.Proactive.AllowedConversationTypes.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    "Microsoft365Copilot:Proactive:AllowedConversationTypes must contain at least one non-empty conversation type.");
            }
        }

        if (options.TokenValidation.Enabled)
        {
            // Outbound credentials are only required when we synthesize the connections ourselves;
            // hosts may configure the Agents SDK "Connections" section natively instead.
            var synthesizesConnection = !configuration.GetSection("Connections").Exists();
            foreach (var agent in agents)
            {
                if (string.IsNullOrWhiteSpace(agent.ClientId))
                {
                    throw new InvalidOperationException(
                        $"Microsoft365Copilot: agent '{agent.Name}' needs a ClientId when TokenValidation is enabled " +
                        "(it is the token audience its route accepts). For local development against the " +
                        "Agents Playground set Microsoft365Copilot:TokenValidation:Enabled = false.");
                }

                if (synthesizesConnection
                    && string.Equals(agent.AuthType, "ClientSecret", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(agent.ClientSecret))
                {
                    throw new InvalidOperationException(
                        $"Microsoft365Copilot: agent '{agent.Name}' needs a ClientSecret for AuthType 'ClientSecret' when token " +
                        "validation is enabled. Use certificate or managed-identity AuthType values to avoid secrets.");
                }
            }
        }
    }

    /// <summary>
    /// Registered by <see cref="AddMicrosoft365Copilot"/> so Use can verify ordering, and so the
    /// administration and reporting components can say why the channel is off.
    /// </summary>
    /// <param name="Enabled">Whether the channel serves its messaging endpoints.</param>
    /// <param name="Configured">False when neither a configuration section nor a code delegate was supplied.</param>
    /// <param name="DisabledReason">Why the channel is off. Null while it is on.</param>
    /// <param name="NativeServiceConnection">The host defined the Agents SDK <c>Connections</c> section itself.</param>
    internal sealed record Microsoft365CopilotMarker(
        bool Enabled, bool Configured = true, string? DisabledReason = null, bool NativeServiceConnection = false)
    {
        /// <summary>Single-agent settings found beside an <c>Agents</c> list, and so not read.</summary>
        public IReadOnlyList<string> IgnoredLegacyKeys { get; init; } = [];
    }
}
