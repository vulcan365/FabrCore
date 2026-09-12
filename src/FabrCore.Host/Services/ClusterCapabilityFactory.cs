using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Host.Configuration;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.Memory.Administration;
using FabrCore.Services.GraphRag.Administration;

namespace FabrCore.Host.Services;

internal static class ClusterCapabilityFactory
{
    internal static ClusterCapabilityDocument Create(FabrCoreFeatureState features, IEnumerable<IBlueprintExpander> expanders, RemoteAdministrationOptions options)
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
            Features=["runtime","blueprints","skills","audit","monitor","evidence","capabilities"]
        });
        if (features.DatabaseEnabled)
        {
            document.Services[0].Features.Add("acl");
            document.Services[0].Features.Add("principal-description");
            document.Services.Add(new() {Name="memory",Version=document.HostVersion,ApiVersion=MemoryAdminCapability.CurrentApiVersion,Features=["dashboard","scopes","memories","consolidation","audit"]});
            document.Services.Add(new() {Name="graphrag",Version=document.HostVersion,ApiVersion=GraphRagAdminCapability.CurrentApiVersion,MaxRequestBodyBytes=options.MaxBodyBytes,Features=["dashboard","scopes","documents","entities","relationships","taxonomy","graph","search","metrics","maintenance","upload"]});
        }
        return document;
    }
}
