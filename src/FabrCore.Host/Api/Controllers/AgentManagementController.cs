using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Security;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;
using System.Text.Json.Schema;
using FabrCore.Core.Blueprints;

namespace FabrCore.Host.Api.Controllers;

[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/principals/{principalId}/agents")]
public sealed class AgentManagementController(IClusterClient cluster, IFabrCoreAgentService agents, IFabrCoreConfigurationStore configuration,
    IEnumerable<IBlueprintExpander> expanders, IFabrCoreSkillCatalogService skills) : ControllerBase
{
    [HttpGet("registrations")]
    public async Task<IActionResult> Registrations(string principalId, int offset = 0, int limit = 100, string? revision = null)
    {
        var tracked = await cluster.GetGrain<IPrincipalGrain>(principalId).GetTrackedAgents(false);
        var entries = tracked.OrderBy(a => a.Handle, StringComparer.Ordinal)
            .Select(a => new { a.RegistrationId, a.Handle, a.AgentType }).ToArray();
        var current = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)));
        if (revision is not null && current != revision) return StatusCode(412);
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Clamp(offset, 0, entries.Length);
        return Ok(new { items = entries.Skip(offset).Take(limit), revision = current,
            nextCursor = offset + limit < entries.Length ? (offset + limit).ToString() : null });
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(string principalId, CancellationToken cancellationToken) => Ok(new { agents = agents.GetAgentTypes(), plugins = agents.GetPlugins(), tools = agents.GetTools(),
        models = (await configuration.GetConfigurationAsync()).ModelConfigurations.Select(m => new { m.Name, m.Model, m.Provider }),
        skills = await skills.ListAsync(principalId, cancellationToken),
        extensions = expanders.Select(e => new { key = e.ExtensionKey, previewSupported = e is IBlueprintPreviewExpander, schema = (e as IBlueprintPreviewExpander)?.ConfigurationSchema }),
        agentSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(AgentConfiguration)) });
    [HttpPost]
    public async Task<IActionResult> Create(string principalId, List<AgentConfiguration> configurations)
    {
        if (configurations.Any(c => string.IsNullOrWhiteSpace(c.Handle) || c.Handle.Contains(':'))) return BadRequest(new { error = "Use local agent handles." });
        return Ok(await agents.ConfigureAgentsAsync(principalId, configurations.Select(c => { c.ForceReconfigure = false; return c; }).ToList()));
    }
    [HttpGet("{agentHandle}/configuration")]
    public Task<IActionResult> Get(string principalId, string agentHandle) => Dispatch(principalId, agentHandle, "get", new());
    [HttpPost("{agentHandle}/actions/{managementAction}")]
    public Task<IActionResult> Action(string principalId, string agentHandle, string managementAction, AgentManagementRequest request) => Dispatch(principalId, agentHandle, managementAction, request);
    private async Task<IActionResult> Dispatch(string principalId, string agentHandle, string operation, AgentManagementRequest request)
    {
        if (agentHandle.Contains(':')) return BadRequest(new { error = "Use a local agent handle." });
        var json = await cluster.GetGrain<IAgentGrain>($"{principalId}:{agentHandle}").ManageAsync(operation, JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        var response = JsonSerializer.Deserialize<AdminApiResult>(json, JsonSerializerOptions.Web)!;
        return StatusCode(response.StatusCode, response.Body);
    }
}
