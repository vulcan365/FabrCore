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

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// FabrCore server addon that exposes the host's agents to Microsoft 365 Copilot and Teams.
/// Call <see cref="AddMicrosoft365Copilot"/> after <c>AddFabrCoreServer</c> and
/// <see cref="UseMicrosoft365Copilot"/> after <c>UseFabrCoreServer</c>:
/// <code>
/// builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies = [typeof(MyAgent).Assembly] });
/// builder.AddMicrosoft365Copilot();
///
/// var app = builder.Build();
/// app.UseFabrCoreServer();
/// app.UseMicrosoft365Copilot();
/// app.Run();
/// </code>
/// Configuration comes from the <c>Microsoft365Copilot</c> section of fabrcore.json or
/// appsettings.json. No changes to FabrCore.Host are required.
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
        // The Microsoft365Copilot section may live in fabrcore.json, which the FabrCore host does
        // not load into IConfiguration by itself. Pull it in when the section is not already
        // present from appsettings.json (or a host-added fabrcore.json).
        if (!builder.Configuration.GetSection(Microsoft365CopilotDefaults.SectionName).Exists())
        {
            builder.Configuration.AddJsonFile("fabrcore.json", optional: true, reloadOnChange: true);
        }

        var section = builder.Configuration.GetSection(Microsoft365CopilotDefaults.SectionName);
        var options = section.Get<Microsoft365CopilotOptions>() ?? new Microsoft365CopilotOptions();
        configure?.Invoke(options);
        options.UserAuthorizationConfigured = section.GetSection("UserAuthorization:Handlers").Exists()
            || builder.Configuration.GetSection("AgentApplication:UserAuthorization:Handlers").Exists();

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(new Microsoft365CopilotMarker(options.Enabled));

        if (!options.Enabled)
        {
            return builder;
        }

        Validate(options, builder.Configuration);

        // Feed the Microsoft 365 Agents SDK the configuration shape it expects
        // (Connections / ConnectionsMap / AgentApplication) from our single section.
        AgentsSdkConfigurationBridge.Inject(builder.Configuration, options, section);

        builder.Services.AddHttpClient();

        // Turn/sign-in state store for the Agents SDK. In-memory by default; hosts that scale
        // out or use SSO in production should register a durable IStorage before this call.
        builder.Services.TryAddSingleton<IStorage, MemoryStorage>();

        builder.Services.TryAddSingleton<ICopilotPrincipalResolver, DefaultCopilotPrincipalResolver>();
        builder.Services.TryAddSingleton<ICopilotAgentProvisioner, CopilotAgentProvisioner>();
        builder.Services.AddSingleton<CopilotAppPackageBuilder>();

        // Registers the CloudAdapter (IAgentHttpAdapter), IConnections, channel client factory,
        // background activity queue, and the bridge agent itself.
        builder.AddAgent<FabrCoreCopilotAgent>();

        if (options.TokenValidation.Enabled)
        {
            builder.Services.AddCopilotChannelAuthentication(options);
        }

        return builder;
    }

    /// <summary>
    /// Maps the Azure Bot Service messaging endpoint (default <c>/api/messages</c>) and, in
    /// development (or when explicitly enabled), the app-package download endpoints under
    /// <c>/m365copilot</c>.
    /// </summary>
    public static WebApplication UseMicrosoft365Copilot(this WebApplication app)
    {
        var marker = app.Services.GetService<Microsoft365CopilotMarker>()
            ?? throw new InvalidOperationException(
                "UseMicrosoft365Copilot requires AddMicrosoft365Copilot to be called on the builder first.");

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FabrCore.Services.Microsoft365Copilot");

        if (!marker.Enabled)
        {
            logger.LogInformation("Microsoft 365 Copilot addon is disabled (Microsoft365Copilot:Enabled = false).");
            return app;
        }

        var options = app.Services.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value;

        var messages = app.MapPost(
            options.MessagesEndpoint,
            (HttpRequest request, HttpResponse response, IAgentHttpAdapter adapter, IAgent agent, CancellationToken cancellationToken)
                => adapter.ProcessAsync(request, response, agent, cancellationToken));

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
                options.MessagesEndpoint);
        }

        if (options.Manifest.EnableAppPackageEndpoint ?? app.Environment.IsDevelopment())
        {
            MapAppPackageEndpoints(app);
            logger.LogInformation(
                "Microsoft 365 app package available at {PackageRoute} (manifest at {ManifestRoute}).",
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/appPackage.zip",
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/manifest.json");
        }

        logger.LogInformation(
            "Microsoft 365 Copilot channel ready: endpoint {Endpoint}, agent {Agent}, token validation {TokenValidation}, user SSO {Sso}.",
            options.MessagesEndpoint,
            options.Agent.SharedAgentHandle ?? $"{options.Agent.AgentType} (per user, handle '{options.Agent.Handle}')",
            options.TokenValidation.Enabled ? "on" : "OFF",
            options.UserAuthorizationConfigured ? "on" : "off");

        return app;
    }

    private static void MapAppPackageEndpoints(WebApplication app)
    {
        app.MapGet(
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/manifest.json",
                (CopilotAppPackageBuilder packageBuilder)
                    => BuildPackageResult(() => Results.Text(packageBuilder.BuildManifestJson(), "application/json")))
            .AllowAnonymous();

        app.MapGet(
                Microsoft365CopilotDefaults.AppPackageRoutePrefix + "/appPackage.zip",
                (CopilotAppPackageBuilder packageBuilder)
                    => BuildPackageResult(() => Results.File(packageBuilder.BuildPackageZip(), "application/zip", "appPackage.zip")))
            .AllowAnonymous();

        static IResult BuildPackageResult(Func<IResult> build)
        {
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

    private static void Validate(Microsoft365CopilotOptions options, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(options.Agent.AgentType)
            && string.IsNullOrWhiteSpace(options.Agent.SharedAgentHandle))
        {
            throw new InvalidOperationException(
                "Microsoft365Copilot: configure Agent:AgentType (a FabrCore [AgentAlias] value) " +
                "or Agent:SharedAgentHandle so inbound Copilot messages have a FabrCore agent to route to.");
        }

        if (!string.IsNullOrWhiteSpace(options.Agent.SharedAgentHandle)
            && !options.Agent.SharedAgentHandle.Contains(':'))
        {
            throw new InvalidOperationException(
                "Microsoft365Copilot: Agent:SharedAgentHandle must be fully qualified " +
                "(\"principalHandle:agentHandle\", e.g. \"system:assistant\").");
        }

        if (options.Principal.Prefix?.Contains(':') == true)
        {
            throw new InvalidOperationException(
                "Microsoft365Copilot: Principal:Prefix must not contain ':' — it is the FabrCore handle separator.");
        }

        if (options.TokenValidation.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.ClientId) && options.TokenValidation.Audiences.Count == 0)
            {
                throw new InvalidOperationException(
                    "Microsoft365Copilot: ClientId is required when TokenValidation is enabled " +
                    "(it is the expected token audience). For local development against the " +
                    "Agents Playground set Microsoft365Copilot:TokenValidation:Enabled = false.");
            }

            // Outbound credentials are only required when we synthesize the connection ourselves;
            // hosts may configure the Agents SDK "Connections" section natively instead.
            var synthesizesConnection = !configuration.GetSection("Connections").Exists();
            if (synthesizesConnection
                && string.Equals(options.AuthType, "ClientSecret", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                throw new InvalidOperationException(
                    "Microsoft365Copilot: ClientSecret is required for AuthType 'ClientSecret' when token " +
                    "validation is enabled. Use certificate or managed-identity AuthType values to avoid secrets.");
            }
        }
    }

    /// <summary>Registered by <see cref="AddMicrosoft365Copilot"/> so Use can verify ordering.</summary>
    internal sealed record Microsoft365CopilotMarker(bool Enabled);
}
