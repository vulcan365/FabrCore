using FabrCore.Core.Blueprints;
using FabrCore.Host.Security;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.Memory.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FabrCore.Host.Configuration;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/capabilities")]
public sealed class CapabilityController(
    IServiceProvider services,
    IEnumerable<IBlueprintExpander> blueprintExpanders,
    IOptions<CloudServerOptions> cloudOptions) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var document = new ClusterCapabilityDocument
        {
            HostVersion = typeof(CapabilityController).Assembly
                .GetName()
                .Version?
                .ToString() ?? "unknown",
            MaxRequestBodyBytes = cloudOptions.Value.RemoteAdministration.MaxBodyBytes,
            BlueprintExtensions = blueprintExpanders
                .Select(expander => expander.ExtensionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList()
        };

        document.Services.Add(new ClusterServiceCapability
        {
            Name = "host-admin",
            Version = document.HostVersion,
            ApiVersion = "1",
            DataScope = "cluster",
            MaxRequestBodyBytes = document.MaxRequestBodyBytes,
            Features = ["runtime", "blueprints", "acl", "audit", "monitor", "evidence"]
        });

        if (services.GetService<IMemoryAdminService>() is not null)
        {
            document.Services.Add(new ClusterServiceCapability
            {
                Name = "memory",
                Version = typeof(IMemoryAdminService).Assembly.GetName().Version?.ToString(),
                ApiVersion = MemoryAdminCapability.CurrentApiVersion,
                Features = ["dashboard", "scopes", "memories", "consolidation", "audit"]
            });
        }

        if (services.GetService<IGraphRagAdminService>() is not null)
        {
            document.Services.Add(new ClusterServiceCapability
            {
                Name = "graphrag",
                Version = typeof(IGraphRagAdminService).Assembly.GetName().Version?.ToString(),
                ApiVersion = GraphRagAdminCapability.CurrentApiVersion,
                Features =
                [
                    "dashboard", "scopes", "documents", "entities", "relationships",
                    "taxonomy", "graph", "search", "metrics", "maintenance", "upload"
                ]
            });
        }

        return Ok(document);
    }
}
