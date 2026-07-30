using FabrCore.Core;
using FabrCore.SampleApp.Components;
using FabrCore.SampleApp.Contoso;
using FabrCore.SampleApp.Crm;
using FabrCore.SampleApp.Surface;
using FabrCore.Host;
using FabrCore.Services.GraphRag;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Microsoft365Copilot;
using FabrCore.Surface;
using FabrCore.Surface.Actions;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Options;

namespace FabrCore.SampleApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddSingleton<InMemoryCrmStore>();
            builder.Services.AddSingleton<InMemorySurfaceDemoDomainStore>();
            builder.Services.AddSingleton<ContosoBikeShopStore>();
            builder.Services.AddSingleton<ISurfaceActionRegistry, CrmSurfaceActionRegistry>();

            var fabrCoreOptions = new FabrCoreServerOptions
            {
                AdditionalAssemblies =
                [
                    typeof(CrmDemoAgent).Assembly,
                    typeof(SurfaceMessageTypes).Assembly
                ]
            };

            fabrCoreOptions.UseInMemoryAgentMessageMonitor(capture =>
            {
                capture.CapturePayloads = true;
                capture.MaxPayloadChars = 8_000;
                capture.MaxToolArgsChars = 4_000;
            });
            fabrCoreOptions
                .UseVerifiableExecution()
                .UseLocalCertificateVerifiableExecutionSigner();

            builder.AddFabrCoreServer(fabrCoreOptions);

            // Microsoft 365 Copilot / Teams channel (/api/messages). Configured from the
            // Microsoft365Copilot section of fabrcore.json.
            builder.AddMicrosoft365Copilot();

            builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "crm-demo");

            // Optional SQL-backed services are inert unless their connection strings exist.
            if (builder.Configuration.GetConnectionString("MemoryDb") is { Length: > 0 })
            {
                builder.Services.AddAgentMemoryServices("MemoryDb");
                builder.Services.AddMemoryAdministration();
            }
            if (builder.Configuration.GetConnectionString("GraphRagDb") is { Length: > 0 })
            {
                builder.Services.AddGraphRagServices("GraphRagDb");
                builder.Services.AddGraphRagAdministration();
            }
            builder.Services.Configure<SurfaceOptions>(options =>
            {
                options.DevelopmentFallbackPrincipalId = SurfaceDemoBootstrapper.PrincipalHandle;
                options.EnableDiagnosticsPanel = true;
                options.EnableAgentCreate = false;
                options.DefaultSurfaceAgentHandles.Add(SurfaceDemoBootstrapper.CrmAgentHandle);
            });
            builder.Services.AddHostedService<SurfaceDemoBootstrapper>();

            var app = builder.Build();

            app.MapDefaultEndpoints();

            var copilotOptions = app.Services
                .GetRequiredService<IOptions<Microsoft365CopilotOptions>>()
                .Value;
            var principalRelayChannels = app.Services
                .GetServices<IPrincipalMessageRelay>()
                .Select(relay => relay.Channel)
                .ToArray();
            var copilotRelayRegistered = principalRelayChannels.Contains(
                "m365copilot",
                StringComparer.OrdinalIgnoreCase);

            app.Logger.LogInformation(
                "Microsoft 365 proactive delivery startup - Enabled: {Enabled}, AllowedConversationTypes: {AllowedConversationTypes}, RelayRegistered: {RelayRegistered}, RelayChannels: {RelayChannels}",
                copilotOptions.Proactive.Enabled,
                string.Join(",", copilotOptions.Proactive.AllowedConversationTypes),
                copilotRelayRegistered,
                string.Join(",", principalRelayChannels));

            if (copilotOptions.Proactive.Enabled && !copilotRelayRegistered)
            {
                app.Logger.LogWarning(
                    "Microsoft 365 proactive delivery is enabled, but its principal message relay is not registered.");
            }

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseAntiforgery();
            app.UseFabrCoreServer(new FabrCoreServerOptions());
            app.UseMicrosoft365Copilot();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .AddFabrCoreSurfaceRoutes();

            app.Run();
        }
    }
}
