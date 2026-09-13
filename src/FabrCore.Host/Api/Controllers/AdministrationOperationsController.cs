using System.Text.Json;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;
namespace FabrCore.Host.Api.Controllers;
[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/principals/{principalId}/operations/{operationId}")]
public sealed class AdministrationOperationsController(IClusterClient cluster) : ControllerBase
{
    [HttpPut] public async Task<IActionResult> Start(string principalId, string operationId, AdministrationOperationRequest request) =>
        ValidId(operationId) ? Reply(await Grain(principalId, operationId).StartAsync(JsonSerializer.Serialize(request, JsonSerializerOptions.Web))) : BadRequest(new { error = "Operation IDs must contain 1–128 characters." });
    [HttpGet] public async Task<IActionResult> Read(string principalId, string operationId) => ValidId(operationId)
        ? Reply(await Grain(principalId, operationId).ReadAsync()) : BadRequest(new { error = "Invalid operation ID." });
    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128;
    private IAdministrationOperationGrain Grain(string principal, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Operation IDs must contain 1–128 characters.");
        var actor = Request.Headers[RemoteAdminController.ActorHeader].FirstOrDefault() ?? User.Identity!.Name!;
        return cluster.GetGrain<IAdministrationOperationGrain>(JsonSerializer.Serialize(new[] { principal, actor, id }));
    }
    private IActionResult Reply(string json)
    {
        var result = JsonSerializer.Deserialize<AdminApiResult>(json, JsonSerializerOptions.Web)!;
        return StatusCode(result.StatusCode, result.Body);
    }
}
