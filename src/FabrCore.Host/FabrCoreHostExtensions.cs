using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.Monitoring;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FabrCore.Host
{

    public class FabrCoreServerOptions
    {
        public List<Assembly> AdditionalAssemblies { get; set; } = new();

        internal TimeProviderRegistration TimeProviderRegistrationOptions { get; private set; } = TimeProviderRegistration.None;

        /// <summary>
        /// The implementation type for <see cref="IAgentManagementProvider"/>.
        /// Defaults to <see cref="OrleansAgentManagementProvider"/> which delegates to
        /// the <c>AgentManagementGrain</c> Orleans grain.
        /// </summary>
        internal Type AgentManagementProviderType { get; private set; } = typeof(OrleansAgentManagementProvider);

        /// <summary>
        /// The implementation type for <see cref="IAclEvaluator"/>.
        /// Defaults to <see cref="AclEvaluator"/>, the synchronous snapshot-backed evaluator.
        /// </summary>
        internal Type AclEvaluatorType { get; private set; } = typeof(AclEvaluator);

        /// <summary>
        /// The implementation type for <see cref="IAuditProvider"/>.
        /// Defaults to <see cref="InMemoryAuditProvider"/> so ACL denials and boundary crossings
        /// are visible out of the box. Call <see cref="UseNullAuditProvider"/> to disable.
        /// </summary>
        internal Type AuditProviderType { get; private set; } = typeof(InMemoryAuditProvider);

        /// <summary>
        /// The implementation type for <see cref="IAgentMessageMonitor"/>.
        /// Null by default (monitoring disabled). Call <see cref="UseAgentMessageMonitor{T}"/>
        /// or <see cref="UseInMemoryAgentMessageMonitor"/> to enable.
        /// </summary>
        internal Type? AgentMessageMonitorType { get; private set; }

        internal Type VerifiableExecutionStoreType { get; private set; } = typeof(InMemoryVerifiableExecutionStore);
        internal Type VerifiableExecutionSignerType { get; private set; } = typeof(NullVerifiableExecutionSigner);
        internal VerifiableExecutionOptions VerifiableExecutionOptions { get; } = new();

        /// <summary>
        /// Explicitly registered Orleans provider. When null, <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>
        /// auto-discovers a provider matching <see cref="OrleansClusterOptions.ClusteringMode"/> by convention.
        /// </summary>
        internal IFabrCoreOrleansProvider? OrleansProvider { get; private set; }

        /// <summary>
        /// Gets or sets an Orleans silo configuration callback which runs after FabrCore's
        /// selected clustering provider and before FabrCore grain registration. Use this to
        /// configure transport TLS and other provider-independent Orleans features without
        /// calling <c>UseOrleans</c> a second time.
        /// </summary>
        public Action<ISiloBuilder>? PostConfigureOrleans { get; set; }

        /// <summary>
        /// Options controlling LLM call capture behavior. Registered as a singleton and consumed
        /// by <see cref="InMemoryAgentMessageMonitor"/> and <c>TokenTrackingChatClient</c>.
        /// </summary>
        internal LlmCaptureOptions LlmCaptureOptions { get; } = new LlmCaptureOptions();

        /// <summary>
        /// Configures a custom <see cref="IAgentManagementProvider"/> implementation for
        /// agent/client registration, tracking, and lifecycle management.
        /// </summary>
        /// <typeparam name="T">The provider implementation type.</typeparam>
        public FabrCoreServerOptions UseAgentManagementProvider<T>() where T : class, IAgentManagementProvider
        {
            AgentManagementProviderType = typeof(T);
            return this;
        }

        /// <summary>
        /// Configures a custom <see cref="IAclEvaluator"/> implementation for access control
        /// decisions. The default is <see cref="AclEvaluator"/>. Custom evaluators must honor
        /// the synchronous no-I/O contract — they run inside grain turns on the message hot path.
        /// </summary>
        /// <typeparam name="T">The evaluator implementation type.</typeparam>
        public FabrCoreServerOptions UseAclEvaluator<T>() where T : class, IAclEvaluator
        {
            AclEvaluatorType = typeof(T);
            return this;
        }

        /// <summary>
        /// Configures a custom <see cref="IAuditProvider"/> implementation for security audit
        /// events (database, SIEM, event hub, etc.). The default is <see cref="InMemoryAuditProvider"/>.
        /// </summary>
        /// <typeparam name="T">The provider implementation type.</typeparam>
        public FabrCoreServerOptions UseAuditProvider<T>() where T : class, IAuditProvider
        {
            AuditProviderType = typeof(T);
            return this;
        }

        /// <summary>
        /// Uses the built-in <see cref="InMemoryAuditProvider"/> (the default): a bounded FIFO
        /// buffer sized by <c>FabrCore:Audit:MaxBufferedEvents</c>.
        /// </summary>
        public FabrCoreServerOptions UseInMemoryAuditProvider()
        {
            AuditProviderType = typeof(InMemoryAuditProvider);
            return this;
        }

        /// <summary>
        /// Disables security audit recording entirely.
        /// </summary>
        public FabrCoreServerOptions UseNullAuditProvider()
        {
            AuditProviderType = typeof(NullAuditProvider);
            return this;
        }

        /// <summary>
        /// Configures the <see cref="TimeProvider"/> used by Orleans for silo scheduling,
        /// timers, and reminders.
        /// </summary>
        /// <param name="provider">The time provider instance to register as a singleton.</param>
        public FabrCoreServerOptions UseTimeProvider(TimeProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            TimeProviderRegistrationOptions = TimeProviderRegistration.ForInstance(provider);
            return this;
        }

        /// <summary>
        /// Configures the <see cref="TimeProvider"/> implementation used by Orleans for
        /// silo scheduling, timers, and reminders.
        /// </summary>
        /// <typeparam name="TTimeProvider">The singleton time provider implementation type.</typeparam>
        public FabrCoreServerOptions UseTimeProvider<TTimeProvider>() where TTimeProvider : TimeProvider
        {
            TimeProviderRegistrationOptions = TimeProviderRegistration.ForType(typeof(TTimeProvider));
            return this;
        }

        /// <summary>
        /// Configures a custom <see cref="IAgentMessageMonitor"/> implementation for
        /// agent message monitoring.
        /// </summary>
        /// <typeparam name="T">The monitor implementation type.</typeparam>
        public FabrCoreServerOptions UseAgentMessageMonitor<T>() where T : class, IAgentMessageMonitor
        {
            AgentMessageMonitorType = typeof(T);
            return this;
        }

        /// <summary>
        /// Enables agent message monitoring with the built-in <see cref="InMemoryAgentMessageMonitor"/>.
        /// Messages are stored in a bounded FIFO buffer (default 5000) with accumulated token tracking.
        /// </summary>
        /// <param name="configureLlmCapture">
        /// Optional callback to configure LLM call capture. By default only metadata
        /// (model, tokens, duration, finish reason) is captured. Set
        /// <see cref="LlmCaptureOptions.CapturePayloads"/> to true to also capture prompts and
        /// responses — be sure to configure <see cref="LlmCaptureOptions.Redact"/> when doing so,
        /// as prompts may contain PII or secrets.
        /// </param>
        public FabrCoreServerOptions UseInMemoryAgentMessageMonitor(Action<LlmCaptureOptions>? configureLlmCapture = null)
        {
            AgentMessageMonitorType = typeof(InMemoryAgentMessageMonitor);
            configureLlmCapture?.Invoke(LlmCaptureOptions);
            return this;
        }

        /// <summary>
        /// Registers an Orleans clustering/persistence/reminders/streaming provider for
        /// <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>. Provider packages expose
        /// shorthand extensions (e.g. <c>UseSqlServer()</c> from FabrCore.Host.SqlServer,
        /// <c>UseAzureStorage()</c> from FabrCore.Host.AzureStorage). Explicit registration is
        /// optional — referencing a provider package and setting <c>Orleans:ClusteringMode</c>
        /// is enough for convention-based discovery.
        /// </summary>
        public FabrCoreServerOptions UseOrleansProvider(IFabrCoreOrleansProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            OrleansProvider = provider;
            return this;
        }

        /// <summary>Sets <see cref="PostConfigureOrleans"/>.</summary>
        public FabrCoreServerOptions ConfigureOrleans(Action<ISiloBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            PostConfigureOrleans = configure;
            return this;
        }

        public FabrCoreServerOptions UseVerifiableExecution(Action<VerifiableExecutionOptions>? configure = null)
        {
            VerifiableExecutionOptions.Enabled = true;
            configure?.Invoke(VerifiableExecutionOptions);
            return this;
        }

        public FabrCoreServerOptions UseVerifiableExecutionStore<T>() where T : class, IVerifiableExecutionStore
        {
            VerifiableExecutionStoreType = typeof(T);
            return this;
        }

        public FabrCoreServerOptions UseVerifiableExecutionSigner<T>() where T : class, IVerifiableExecutionSigner
        {
            VerifiableExecutionSignerType = typeof(T);
            return this;
        }

        public FabrCoreServerOptions UseLocalCertificateVerifiableExecutionSigner()
        {
            VerifiableExecutionSignerType = typeof(LocalCertificateVerifiableExecutionSigner);
            return this;
        }

        internal sealed class TimeProviderRegistration
        {
            public static TimeProviderRegistration None { get; } = new(null, null);

            private TimeProviderRegistration(TimeProvider? instance, Type? implementationType)
            {
                Instance = instance;
                ImplementationType = implementationType;
            }

            public TimeProvider? Instance { get; }
            public Type? ImplementationType { get; }
            public bool IsConfigured => Instance is not null || ImplementationType is not null;

            public static TimeProviderRegistration ForInstance(TimeProvider instance)
                => new(instance, null);

            public static TimeProviderRegistration ForType(Type implementationType)
                => new(null, implementationType);
        }
    }

    /// <summary>
    /// Obsolete alias for FabrCoreServerOptions. Use FabrCoreServerOptions instead.
    /// </summary>
    [Obsolete("Use FabrCoreServerOptions instead.")]
    public class StandaloneServerOptions : FabrCoreServerOptions { }

    public static class FabrCoreHostExtensions
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.Extensions");
        private static readonly Meter Meter = new("FabrCore.Host.Extensions");
        private static int _fabrCoreListenerRegistered;

        /// <summary>
        /// Registers a process-wide <see cref="ActivityListener"/> for all FabrCore.* ActivitySources
        /// so that <c>ActivitySource.StartActivity</c> calls actually materialize <see cref="Activity"/>
        /// instances (and therefore populate <c>Activity.Current.TraceId</c> / <c>SpanId</c>). Without this,
        /// ingress-point trace stamping is a no-op. Consumers can still add their own OpenTelemetry
        /// TracerProvider with exporters on top — multiple listeners coexist safely.
        /// </summary>
        private static void EnsureFabrCoreActivityListenerRegistered()
        {
            if (Interlocked.Exchange(ref _fabrCoreListenerRegistered, 1) == 1) return;

            ActivitySource.AddActivityListener(new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("FabrCore.", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            });
        }

        // Metrics
        private static readonly Counter<long> ServerConfiguredCounter = Meter.CreateCounter<long>(
            "fabrcore.host.server.configured",
            description: "Number of standalone servers configured");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.host.errors",
            description: "Number of errors encountered in host extensions");

        public static WebApplication UseFabrCoreServer(this WebApplication app, FabrCoreServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("UseFabrCoreServer", ActivityKind.Internal);
            var logger = app.Services.GetRequiredService<ILogger<WebApplication>>();

            activity?.SetTag("options.has_additional_assemblies", options?.AdditionalAssemblies?.Count > 0);
            activity?.SetTag("options.assembly_count", options?.AdditionalAssemblies?.Count ?? 0);

            logger.LogInformation("Configuring FabrCore server");
            logger.LogDebug("Additional assemblies count: {AssemblyCount}", options?.AdditionalAssemblies?.Count ?? 0);

            try
            {
                // Map FabrCore API controllers
                app.MapControllers();
                logger.LogInformation("FabrCore API controllers mapped");

                // Map health endpoints for Kubernetes-style probes.
                // /health/live: process is alive (no tagged checks required).
                // /health/ready: Orleans cluster client is ready (tagged "ready").
                // /health: detailed JSON served by HealthController (registered above).
                app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = _ => false // no checks run — just a liveness ping
                });
                app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("ready")
                });
                logger.LogInformation("Health endpoints mapped (/health, /health/live, /health/ready)");

                // Enable WebSocket support with configured keep-alive interval.
                var hostOptions = app.Services
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.FabrCoreHostOptions>>()
                    .Value;
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = hostOptions.WebSocketKeepAliveInterval,
                });
                logger.LogInformation(
                    "WebSocket support enabled (keep-alive {KeepAlive}, path {Path})",
                    hostOptions.WebSocketKeepAliveInterval, hostOptions.WebSocketPath);

                // Add WebSocket middleware (path resolved from FabrCoreHostOptions).
                app.UseMiddleware<FabrCore.Host.WebSocket.WebSocketMiddleware>();
                logger.LogInformation("WebSocket middleware added");

                logger.LogInformation("FabrCore server configured successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return app;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error configuring FabrCore server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_configuration_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for UseFabrCoreServer. Use UseFabrCoreServer instead.
        /// </summary>
        [Obsolete("Use UseFabrCoreServer instead.")]
        public static WebApplication UseFabrCoreStandaloneServer(this WebApplication app, FabrCoreServerOptions? options = null)
            => UseFabrCoreServer(app, options);

        /// <summary>
        /// Registers FabrCore's non-Orleans services (DI, controllers, background services).
        /// <para>
        /// Use this when you are calling <c>builder.UseOrleans()</c> yourself and using
        /// <see cref="FabrCoreSiloBuilderExtensions.AddFabrCore"/> for the Orleans-specific parts.
        /// </para>
        /// </summary>
        public static WebApplicationBuilder AddFabrCoreServices(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
        {
            EnsureFabrCoreActivityListenerRegistered();

            using var activity = ActivitySource.StartActivity("AddFabrCoreServices", ActivityKind.Internal);
            options ??= new FabrCoreServerOptions();

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("FabrCore.Host.Extensions");

            logger.LogInformation("Adding FabrCore services - Environment: {Environment}", builder.Environment.EnvironmentName);

            try
            {
                builder.Services.AddHttpContextAccessor();
                logger.LogDebug("HttpContextAccessor added");

                ConfigureTimeProvider(builder.Services, options.TimeProviderRegistrationOptions);
                logger.LogDebug("TimeProvider configured for Orleans");

                builder.Services.AddControllers()
                    .AddApplicationPart(typeof(FabrCoreServerOptions).Assembly);
                logger.LogDebug("FabrCore API controllers registered");

                // Gateway discovery performs policy authorization dynamically because the
                // required policy name is host configuration rather than controller metadata.
                builder.Services.AddAuthorization();

                builder.Services.AddSingleton<FabrCore.Sdk.IFabrCoreChatClientService, FabrCore.Sdk.FabrCoreChatClientService>();
                logger.LogDebug("FabrCoreChatClientService added");

                builder.Services.AddSingleton<FabrCore.Sdk.FabrCoreToolRegistry>();
                logger.LogDebug("FabrCoreToolRegistry added");

                builder.Services.AddSingleton<FabrCore.Sdk.IFabrCoreRegistry, FabrCore.Sdk.FabrCoreRegistry>();
                logger.LogDebug("FabrCoreRegistry added");

                // Configure Embeddings
                builder.Services.AddSingleton<IEmbeddings, Embeddings>();

                // Configure typed entity storage over the configured Orleans grain storage provider.
                builder.Services.AddSingleton<OrleansEntityStorageProvider>();
                builder.Services.AddSingleton<IUserScopedFabrCoreStorageProvider>(sp =>
                    sp.GetRequiredService<OrleansEntityStorageProvider>());
                builder.Services.AddSingleton<IFabrCoreStorageProvider>(sp =>
                    sp.GetRequiredService<OrleansEntityStorageProvider>());
                logger.LogDebug("FabrCore typed entity storage configured");

                // Configure File Storage
                builder.Services.Configure<FileStorageSettings>(builder.Configuration.GetSection("FileStorage"));
                builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
                builder.Services.AddHostedService<FileCleanupBackgroundService>();
                logger.LogInformation("File storage services configured");

                // Configure Compaction
                builder.Services.AddSingleton<FabrCore.Sdk.CompactionService>();
                logger.LogDebug("CompactionService added");

                // Configure Agent Management Provider (pluggable — default is Orleans grain-backed)
                builder.Services.AddSingleton(typeof(IAgentManagementProvider), options.AgentManagementProviderType);
                logger.LogDebug("AgentManagementProvider added: {ProviderType}", options.AgentManagementProviderType.Name);

                // Configure ACL (principals/roles/groups/permission grants, persisted via the
                // single-activation AclRegistryGrain through the configured fabrcoreStorage
                // backend). Most FabrCore config files keep ACL at the root ("Acl"); the
                // FabrCore:Acl section remains supported for hosts that wrap settings under a
                // FabrCore node.
                builder.Services.Configure<FabrCoreAclOptions>(builder.Configuration.GetSection("Acl"));
                builder.Services.Configure<FabrCoreAclOptions>(builder.Configuration.GetSection(FabrCoreAclOptions.SectionName));
                builder.Services.AddSingleton<GrainBackedAclEntityStore>();
                builder.Services.AddSingleton<IAclEntityStore>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
                builder.Services.AddSingleton<IAclSnapshotProvider>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
                builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
                builder.Services.AddSingleton(typeof(IAclEvaluator), options.AclEvaluatorType);
                builder.Services.AddSingleton<AclEnforcer>();
                logger.LogDebug("ACL configured: evaluator {EvaluatorType}", options.AclEvaluatorType.Name);

                // The legacy rule-based ACL config shape no longer binds — warn loudly so
                // operators migrate to principals/roles/groups/grants (see fabrcore-acl skill).
                if (builder.Configuration.GetSection("Acl:Rules").GetChildren().Any() ||
                    builder.Configuration.GetSection("FabrCore:Acl:Rules").GetChildren().Any())
                {
                    logger.LogWarning(
                        "Legacy ACL 'Rules' configuration detected and IGNORED. The rule-based ACL was replaced " +
                        "by principals/roles/groups/permission grants — migrate to 'Acl:Seed:Grants' or the ACL " +
                        "management API (see the fabrcore-acl skill for the mapping).");
                }

                // Configure Security Audit provider (default in-memory so denials are visible
                // out of the box; UseNullAuditProvider() opts out).
                builder.Services.Configure<FabrCore.Core.Auditing.AuditOptions>(
                    builder.Configuration.GetSection(FabrCore.Core.Auditing.AuditOptions.SectionName));
                builder.Services.AddSingleton(typeof(FabrCore.Core.Auditing.IAuditProvider), options.AuditProviderType);
                logger.LogInformation("Security audit provider: {ProviderType}", options.AuditProviderType.Name);

                // Bind tunable options (see FabrCoreHostOptions / AgentGrainOptions / PrincipalGrainOptions).
                builder.Services.Configure<Configuration.FabrCoreHostOptions>(
                    builder.Configuration.GetSection(Configuration.FabrCoreHostOptions.SectionName));
                builder.Services.AddOptions<Configuration.GatewayDiscoveryOptions>()
                    .Configure(discovery =>
                        discovery.RequireOrleansTls = !builder.Environment.IsDevelopment())
                    .Bind(builder.Configuration.GetSection(Configuration.GatewayDiscoveryOptions.SectionName))
                    .ValidateOnStart();
                builder.Services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<Configuration.GatewayDiscoveryOptions>,
                        Configuration.GatewayDiscoveryOptionsValidator>());
                builder.Services.TryAddSingleton<Services.IGatewayDiscoverySource, Services.GatewayDiscoverySource>();
                builder.Services.Configure<Configuration.AgentGrainOptions>(
                    builder.Configuration.GetSection(Configuration.AgentGrainOptions.SectionName));
                builder.Services.Configure<Configuration.PrincipalGrainOptions>(
                    builder.Configuration.GetSection(Configuration.PrincipalGrainOptions.SectionName));
                builder.Services.Configure<Configuration.PrincipalContextOptions>(
                    builder.Configuration.GetSection(Configuration.PrincipalContextOptions.SectionName));
                builder.Services.Configure<Configuration.PrincipalDeliveryOptions>(
                    builder.Configuration.GetSection(Configuration.PrincipalDeliveryOptions.SectionName));

                // Pluggable WebSocket authenticator. Default preserves legacy header/query behavior;
                // production apps override via AddFabrCoreServices().Services.AddSingleton<IWebSocketAuthenticator, MyAuthN>().
                builder.Services.TryAddSingleton<WebSocket.IWebSocketAuthenticator, WebSocket.DefaultWebSocketAuthenticator>();

                // Token cost calculator — default reads FabrCore:ModelPricing from config. Hosts
                // can register their own ITokenCostCalculator for dynamic pricing.
                builder.Services.TryAddSingleton<FabrCore.Core.Monitoring.ITokenCostCalculator, Services.ConfigurableTokenCostCalculator>();

                logger.LogDebug("FabrCore runtime options bound");

                // Configure Agent Message Monitor (opt-in — disabled by default)
                // Register LlmCaptureOptions as a singleton so the monitor and
                // TokenTrackingChatClient can pick it up via DI.
                builder.Services.AddSingleton(options.LlmCaptureOptions);
                builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options.VerifiableExecutionOptions));

                if (options.AgentMessageMonitorType is not null)
                {
                    builder.Services.AddSingleton(typeof(IAgentMessageMonitor), options.AgentMessageMonitorType);
                    logger.LogInformation("AgentMessageMonitor enabled: {MonitorType}", options.AgentMessageMonitorType.Name);
                }
                else
                {
                    builder.Services.AddSingleton<IAgentMessageMonitor, NullAgentMessageMonitor>();
                    logger.LogDebug("AgentMessageMonitor not configured — monitoring disabled");
                }

                builder.Services.AddSingleton(typeof(IVerifiableExecutionStore), options.VerifiableExecutionStoreType);
                builder.Services.AddSingleton(typeof(IVerifiableExecutionSigner), options.VerifiableExecutionSignerType);
                builder.Services.AddSingleton<IVerifiableExecutionVerifier, VerifiableExecutionVerifier>();
                builder.Services.AddSingleton<VerifiableExecutionRecorder>();
                builder.Services.AddSingleton<IVerifiableExecutionContext>(sp =>
                    sp.GetRequiredService<VerifiableExecutionRecorder>());
                logger.LogInformation(
                    "Verifiable execution {State} (Store={StoreType}, Signer={SignerType})",
                    options.VerifiableExecutionOptions.Enabled ? "enabled" : "disabled",
                    options.VerifiableExecutionStoreType.Name,
                    options.VerifiableExecutionSignerType.Name);

                // Configure Agent Service
                builder.Services.AddSingleton<IFabrCoreAgentService, FabrCoreAgentService>();
                builder.Services.TryAddSingleton<IPrincipalContextStore, PrincipalContextStore>();
                builder.Services.TryAddSingleton<IPrincipalMessageDeliveryCompletion, PrincipalMessageDeliveryCompletion>();
                builder.Services.TryAddSingleton<PrincipalMessageRelayDispatcher>();
                logger.LogDebug("FabrCoreAgentService added");

                // Configure Agent Registry Cleanup
                builder.Services.AddHostedService<AgentRegistryCleanupService>();
                logger.LogInformation("Agent registry cleanup service configured");

                // Configure Health Checks (wired to endpoints in UseFabrCoreServer).
                // "live" = process is up; "ready" = Orleans cluster client usable.
                builder.Services.AddHealthChecks()
                    .AddCheck<Services.OrleansClusterHealthCheck>(
                        "orleans-cluster",
                        tags: new[] { "ready" });
                logger.LogDebug("Health checks registered");

                logger.LogInformation("FabrCore services added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding FabrCore services");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "services_add_failed"));
                throw;
            }
        }

        internal static void ConfigureTimeProvider(IServiceCollection services, FabrCoreServerOptions.TimeProviderRegistration registration)
        {
            if (registration.Instance is not null)
            {
                services.RemoveAll<TimeProvider>();

                var provider = registration.Instance;
                var providerType = provider.GetType();
                if (providerType != typeof(TimeProvider))
                {
                    services.RemoveAll(providerType);
                    services.AddSingleton(providerType, provider);
                }

                services.AddSingleton<TimeProvider>(provider);
                return;
            }

            if (registration.ImplementationType is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.RemoveAll(registration.ImplementationType);
                services.AddSingleton(registration.ImplementationType);
                services.AddSingleton(typeof(TimeProvider), sp => sp.GetRequiredService(registration.ImplementationType));
                return;
            }

            services.TryAddSingleton(TimeProvider.System);
        }

        /// <summary>
        /// Configures a complete FabrCore server with Orleans silo using <see cref="OrleansClusterOptions"/> from configuration.
        /// <para>
        /// This is the simple path — it calls <see cref="AddFabrCoreServices"/> and configures Orleans internally.
        /// For full Orleans control, use <see cref="AddFabrCoreServices"/> + <c>builder.UseOrleans()</c> +
        /// <see cref="FabrCoreSiloBuilderExtensions.AddFabrCore"/> instead.
        /// </para>
        /// </summary>
        public static WebApplicationBuilder AddFabrCoreServer(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
        {
            using var activity = ActivitySource.StartActivity("AddFabrCoreServer", ActivityKind.Internal);
            options ??= new FabrCoreServerOptions();

            activity?.SetTag("options.assembly_count", options.AdditionalAssemblies.Count);
            activity?.SetTag("environment", builder.Environment.EnvironmentName);

            // Create a temporary logger factory to log during setup
            using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger("FabrCore.Host.Extensions");

            logger.LogInformation("Adding FabrCore server - Environment: {Environment}", builder.Environment.EnvironmentName);
            logger.LogDebug("Additional assemblies to load: {AssemblyCount}", options.AdditionalAssemblies.Count);

            try
            {
                // Register non-Orleans services
                builder.AddFabrCoreServices(options);

                // Load Orleans cluster options from configuration
                var orleansOptions = builder.Configuration
                    .GetSection(OrleansClusterOptions.SectionName)
                    .Get<OrleansClusterOptions>() ?? new OrleansClusterOptions();

                logger.LogInformation("Orleans clustering mode: {ClusteringMode}", orleansOptions.ClusteringMode);
                activity?.SetTag("orleans.clustering_mode", orleansOptions.ClusteringMode.ToString());

                // Resolve the Orleans provider for the configured mode.
                var orleansProvider = ResolveOrleansProvider(options, orleansOptions, logger);

                // Let the provider auto-provision its backing resources (SQL tables,
                // Azure tables/containers/queues, ...) before the silo starts.
                if (orleansOptions.AutoInitDatabase)
                {
                    orleansProvider.Initialize(orleansOptions, builder.Configuration, logger);
                }

                try
                {
                    builder.UseOrleans(siloBuilder =>
                    {
                        using var orleansActivity = ActivitySource.StartActivity("ConfigureOrleans", ActivityKind.Internal);
                        logger.LogInformation("Configuring Orleans silo");

                        orleansProvider.ConfigureSilo(siloBuilder, orleansOptions, builder.Configuration, logger);

                        // Provider-neutral Orleans configuration (notably transport mTLS) must
                        // be applied after the provider and before the silo is finalized.
                        options.PostConfigureOrleans?.Invoke(siloBuilder);

                        logger.LogInformation("Orleans clustering, persistence, reminders, and streaming configured");

                        // Register FabrCore grain assemblies
                        siloBuilder.AddFabrCore(options.AdditionalAssemblies);

                        orleansActivity?.SetStatus(ActivityStatusCode.Ok);
                    });
                }
                catch (System.IO.FileNotFoundException ex) when (ex.FileName?.StartsWith("Orleans.", StringComparison.Ordinal) == true)
                {
                    // Orleans application-part discovery expands [assembly: ApplicationPart("...")]
                    // attributes with AssemblyLoadContext.LoadFromAssemblyName and no error handling.
                    // The Orleans source generator bakes those attributes into every assembly compiled
                    // while a provider assembly was in its compile closure — so assemblies built against
                    // FabrCore.Host 1.0.x (which bundled the SQL Server and Azure Storage providers)
                    // permanently demand those assemblies even though this deployment no longer ships them.
                    throw new InvalidOperationException(
                        $"Orleans failed to load assembly '{ex.FileName}' while configuring the silo. " +
                        "This usually means an assembly in this application (or one of its NuGet dependencies) was " +
                        "compiled against FabrCore.Host 1.0.x, which bundled the Orleans SQL Server and Azure Storage " +
                        "providers. Orleans bakes ApplicationPart references to those provider assemblies into every " +
                        "consuming assembly at compile time. Fix: rebuild all FabrCore-dependent projects and packages " +
                        "against FabrCore.Host 1.1.0 or later. Workaround: reference the provider package that supplies " +
                        "the missing assembly (FabrCore.Host.SqlServer for AdoNet, FabrCore.Host.AzureStorage for Azure).",
                        ex);
                }

                ServerConfiguredCounter.Add(1,
                    new KeyValuePair<string, object?>("environment", builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object?>("assemblies.count", options.AdditionalAssemblies.Count));

                logger.LogInformation("FabrCore server added successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);

                return builder;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding FabrCore server");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "server_add_failed"));
                throw;
            }
        }

        /// <summary>
        /// Obsolete alias for AddFabrCoreServer. Use AddFabrCoreServer instead.
        /// </summary>
        [Obsolete("Use AddFabrCoreServer instead.")]
        public static WebApplicationBuilder AddFabrCoreStandaloneServer(this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)
            => AddFabrCoreServer(builder, options);

        /// <summary>
        /// Resolves the Orleans provider for the configured clustering mode.
        /// Localhost is handled by the built-in <see cref="LocalhostOrleansProvider"/>.
        /// </summary>
        private static IFabrCoreOrleansProvider ResolveOrleansProvider(
            FabrCoreServerOptions options, OrleansClusterOptions orleansOptions, ILogger logger)
        {
            var mode = orleansOptions.ClusteringMode;

            if (mode == ClusteringMode.Localhost)
            {
                if (options.OrleansProvider is not null && options.OrleansProvider.Mode != ClusteringMode.Localhost)
                {
                    logger.LogWarning(
                        "Orleans provider {ProviderType} is registered but Orleans:ClusteringMode is 'Localhost' — the provider is ignored. " +
                        "Set Orleans:ClusteringMode to '{Mode}' to use it.",
                        options.OrleansProvider.GetType().Name, options.OrleansProvider.Mode);
                }
                return options.OrleansProvider?.Mode == ClusteringMode.Localhost
                    ? options.OrleansProvider
                    : new LocalhostOrleansProvider();
            }

            if (options.OrleansProvider is not null)
            {
                if (options.OrleansProvider.Mode != mode)
                {
                    throw new InvalidOperationException(
                        $"The registered Orleans provider '{options.OrleansProvider.GetType().Name}' handles mode " +
                        $"'{options.OrleansProvider.Mode}' but Orleans:ClusteringMode is '{mode}'.");
                }
                return options.OrleansProvider;
            }

            // Convention-based discovery: an assembly named FabrCore.Host.<Mode> containing a
            // public parameterless IFabrCoreOrleansProvider implementation. Referencing the
            // provider package and setting Orleans:ClusteringMode is all a host needs to do.
            var assemblyName = $"FabrCore.Host.{mode}";
            try
            {
                var assembly = Assembly.Load(assemblyName);
                var providerType = assembly.GetExportedTypes().FirstOrDefault(t =>
                    !t.IsAbstract &&
                    typeof(IFabrCoreOrleansProvider).IsAssignableFrom(t) &&
                    t.GetConstructor(Type.EmptyTypes) is not null);

                if (providerType is not null)
                {
                    var provider = (IFabrCoreOrleansProvider)Activator.CreateInstance(providerType)!;
                    if (provider.Mode == mode)
                    {
                        logger.LogInformation("Orleans provider {ProviderType} auto-discovered from {Assembly}",
                            providerType.Name, assemblyName);
                        return provider;
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException)
            {
                // Assembly not referenced — fall through to the guidance below.
            }

            throw new InvalidOperationException(
                $"Orleans:ClusteringMode is '{mode}' but no Orleans provider for that mode is available. " +
                $"Reference the '{assemblyName}' NuGet package (it is auto-discovered), or register a provider " +
                $"explicitly via FabrCoreServerOptions.UseOrleansProvider (e.g. options.Use{mode}()).");
        }
    }
}
