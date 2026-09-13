using System.Text.Json;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/principals/{principalId}/agents/{agentHandle}/admin-sessions")]
public sealed class AdminConversationsController(IClusterClient cluster) : ControllerBase
{
    [HttpGet] public Task<IActionResult> List(string principalId, string agentHandle) => Dispatch(principalId, agentHandle, "list");
    [HttpPost] public Task<IActionResult> Create(string principalId, string agentHandle, AdminSessionRequest request) => Dispatch(principalId, agentHandle, "create", body: request);
    [HttpGet("{sessionId}")] public Task<IActionResult> Get(string principalId, string agentHandle, string sessionId) => Dispatch(principalId, agentHandle, "get", sessionId);
    [HttpDelete("{sessionId}")] public Task<IActionResult> Delete(string principalId, string agentHandle, string sessionId) => Dispatch(principalId, agentHandle, "delete", sessionId);
    [HttpPost("{sessionId}/turns")] public Task<IActionResult> Turn(string principalId, string agentHandle, string sessionId, AdminTurnRequest request) => Dispatch(principalId, agentHandle, "turn", sessionId, request);
    [HttpGet("{sessionId}/turns/{turnId}")] public Task<IActionResult> ReadTurn(string principalId, string agentHandle, string sessionId, string turnId) => Dispatch(principalId, agentHandle, "turn-get", sessionId, turnId);

    private async Task<IActionResult> Dispatch(string principalId, string agentHandle, string operation, string? sessionId = null, object? body = null)
    {
        if (Request.Headers.ContainsKey("x-user-handle") || Request.Headers.ContainsKey("x-user") || Request.Headers.ContainsKey("X-FabrCore-Admin-Target"))
            return BadRequest(new { error = "Targets must be supplied as route parameters." });
        if (agentHandle.Contains(':') || string.IsNullOrWhiteSpace(principalId)) return BadRequest(new { error = "Use a principal and local agent handle." });
        // This header is accepted only after admin authentication; the cloud broker establishes it.
        var actor = Request.Headers[RemoteAdminController.ActorHeader].FirstOrDefault() ?? User.Identity!.Name!;
        var json = await cluster.GetGrain<IAgentGrain>($"{principalId}:{agentHandle}").AdministerAsync(actor, operation, sessionId,
            JsonSerializer.Serialize(body ?? new { }, JsonSerializerOptions.Web));
        var result = JsonSerializer.Deserialize<AdminApiResult>(json, JsonSerializerOptions.Web)!;
        return StatusCode(result.StatusCode, result.Body);
    }
}
