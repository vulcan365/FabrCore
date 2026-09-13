using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Host.Configuration;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.Memory.Administration;
using FabrCore.Services.GraphRag.Administration;

namespace FabrCore.Host.Services;

internal static class ClusterCapabilityFactory
{
    internal static ClusterCapabilityDocument Create(FabrCoreFeatureState features, IEnumerable<IBlueprintExpander> expanders, RemoteAdministrationOptions options, IServiceProvider? providers = null)
    {
        var document = new ClusterCapabilityDocument
        {
            HostVersion = typeof(ClusterCapabilityFactory).Assembly.GetName().Version?.ToString() ?? "unknown",
            MaxRequestBodyBytes = options.MaxBodyBytes,
            BlueprintExtensions = expanders.Select(e=>e.ExtensionKey).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList()
        };
        document.Services.Add(new()
        {
            Name="host-admin", Version=document.HostVersion, ApiVersion="1", MaxRequestBodyBytes=options.MaxBodyBytes,
            Features=["runtime","blueprints","skills","audit","monitor","evidence","capabilities","admin-conversations","agent-management","blueprint-management","monitor-query","evidence-query","operations","principal-directory"]
        });
        if (providers?.GetService(typeof(FabrCore.Core.Monitoring.IAgentMessageMonitor)) is not FabrCore.Core.Monitoring.IAgentMonitorQueryProvider) document.Services[0].Features.Remove("monitor-query");
        if (providers?.GetService(typeof(FabrCore.Core.VerifiableExecution.IVerifiableExecutionStore)) is not FabrCore.Core.VerifiableExecution.IVerifiableExecutionQueryProvider) document.Services[0].Features.Remove("evidence-query");
        if (features.DatabaseEnabled)
        {
            document.Services[0].Features.Add("acl"); document.Services[0].Features.Add("acl-conditional");
            document.Services[0].Features.Add("principal-description");
            document.Services.Add(new() {Name="memory",Version=document.HostVersion,ApiVersion=MemoryAdminCapability.CurrentApiVersion,Features=["dashboard","scopes","memories","consolidation","audit"]});
            document.Services.Add(new() {Name="graphrag",Version=document.HostVersion,ApiVersion=GraphRagAdminCapability.CurrentApiVersion,MaxRequestBodyBytes=options.MaxBodyBytes,Features=["dashboard","scopes","documents","entities","relationships","taxonomy","graph","search","metrics","maintenance","upload"]});
        }
        if (providers?.GetService(typeof(FabrCore.Connections.IConnectionService)) is not null)
        {
            document.Services.Add(new() { Name = "connections", Version = document.HostVersion, ApiVersion = "1", Features = ["profiles", "user-authorization", "application-credentials"] });
            if (providers.GetService(typeof(FabrCore.Connections.ConnectionCapabilities)) is FabrCore.Connections.ConnectionCapabilities connections)
            {
                if (connections.EntraAgentIdEnabled) document.Services[^1].Features.Add("entra-agent-id");
                if (connections.ClientHandoffEnabled) document.Services[^1].Features.Add("encrypted-client-handoff");
            }
        }
        return document;
    }
}
