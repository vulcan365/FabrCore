using System.Text.Json;
using FabrCore.Core.Blueprints;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;
namespace FabrCore.Host.Api.Controllers;
[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/principals/{principalId}/blueprint-management")]
public sealed class BlueprintAdministrationController(IClusterClient cluster) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(string principalId) => Content(await Grain(principalId).ExecuteAsync("summaries", "", "{}", null), "application/json");
    [HttpGet("summaries")] public Task<IActionResult> Summaries(string principalId, int offset = 0, int limit = 100, string? revision = null) =>
        Run(principalId, "summary-page", "", new AgentManagementRequest { Offset = offset, Limit = limit, Revision = revision });
    [HttpPost("validate")] public Task<IActionResult> Validate(string principalId, FabrCoreBlueprint blueprint) => Run(principalId, "validate", "", blueprint);
    [HttpGet("{name}/preview")] public Task<IActionResult> Preview(string principalId, string name) => Run(principalId, "preview", name, new { });
    [HttpPut("{name}")] public Task<IActionResult> Save(string principalId, string name, FabrCoreBlueprint blueprint)
    {
        if (string.IsNullOrWhiteSpace(Request.Headers.IfMatch)) return Task.FromResult<IActionResult>(StatusCode(428, new { error = "If-Match blueprint revision required." }));
        blueprint.Name = name; return Run(principalId, "save", name, blueprint);
    }
    [HttpPost("{name}/deploy")] public Task<IActionResult> Deploy(string principalId, string name, BlueprintDeploymentRequest request) => Run(principalId, "deploy", name, request);
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string principalId, string name)
    {
        if (string.IsNullOrWhiteSpace(Request.Headers.IfMatch)) return StatusCode(428, new { error = "If-Match blueprint revision required." });
        var result = JsonSerializer.Deserialize<JsonElement>(await Grain(principalId).ExecuteAsync("delete", name, "{}", Request.Headers.IfMatch.FirstOrDefault()?.Trim('"')));
        if (result.ValueKind == JsonValueKind.Object) return StatusCode(result.GetProperty("statusCode").GetInt32(), result.GetProperty("body"));
        return result.GetBoolean() ? NoContent() : NotFound();
    }
    private IBlueprintAdministrationGrain Grain(string principal) => cluster.GetGrain<IBlueprintAdministrationGrain>(principal);
    private async Task<IActionResult> Run(string principal, string action, string name, object body)
    {
        try
        {
            var json = await Grain(principal).ExecuteAsync(action, name, JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Request.Headers.IfMatch.FirstOrDefault()?.Trim('"'));
            var response = JsonSerializer.Deserialize<AdminApiResult>(json, JsonSerializerOptions.Web)!;
            return StatusCode(response.StatusCode, response.Body);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
