using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;
namespace FabrCore.Host.Api.Controllers;
[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy), RequiresFabrCoreDatabase]
[Route("fabrcoreapi/admin/v1/access/entities/{kind}")]
public sealed class AclCollectionsController(IClusterClient cluster) : ControllerBase
{
    private IAclRegistryGrain Grain => cluster.GetGrain<IAclRegistryGrain>(IAclRegistryGrain.WellKnownKey);
    [HttpGet]
    public async Task<IActionResult> List(string kind, int offset = 0, int limit = 100, long? version = null)
    {
        var snapshot = await Grain.GetSnapshotAsync();
        if (version is not null && version != snapshot.Version) return StatusCode(412, new { error = "ACL changed; restart pagination." });
        var root = JsonSerializer.SerializeToElement(snapshot, JsonSerializerOptions.Web);
        if (kind is not ("principals" or "roles" or "groups" or "grants")) return BadRequest();
        var items = root.GetProperty(kind).EnumerateArray().OrderBy(item => item.GetProperty(Key(kind)).GetString(), StringComparer.OrdinalIgnoreCase).ToList();
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 1000);
        Response.Headers.ETag = $"\"{snapshot.Version}\"";
        return Ok(new AdministrationPage<JsonElement> { Items = items.Skip(offset).Take(limit).ToList(), Revision = snapshot.Version.ToString(),
            NextCursor = offset + limit < items.Count ? (offset + limit).ToString() : null });
    }
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string kind, string id)
    {
        if (kind is not ("principals" or "roles" or "groups" or "grants")) return BadRequest();
        var snapshot = await Grain.GetSnapshotAsync();
        var root = JsonSerializer.SerializeToElement(snapshot, JsonSerializerOptions.Web);
        var value = root.GetProperty(kind).EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty(Key(kind)).GetString(), id, StringComparison.OrdinalIgnoreCase));
        Response.Headers.ETag = $"\"{snapshot.Version}\"";
        return value.ValueKind == JsonValueKind.Undefined ? NotFound() : Ok(value);
    }
    private static string Key(string kind) => kind switch { "principals" => "handle", "grants" => "id", _ => "name" };
    [HttpPut("{id}")] public Task<IActionResult> Put(string kind, string id, JsonElement body) => Mutate(kind, id, body.GetRawText());
    [HttpDelete("{id}")] public Task<IActionResult> Delete(string kind, string id) => Mutate(kind, id, null);
    private async Task<IActionResult> Mutate(string kind, string id, string? body)
    {
        if (!long.TryParse(Request.Headers.IfMatch.ToString().Trim('"'), out var version)) return StatusCode(428, new { error = "If-Match ACL version required." });
        try
        {
            var result = JsonSerializer.Deserialize<JsonElement>(await Grain.ConditionalMutationAsync(kind, id, body, version));
            Response.Headers.ETag = $"\"{result.GetProperty("version")}\"";
            return StatusCode(result.GetProperty("statusCode").GetInt32(), result);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) { return BadRequest(new { error = ex.Message }); }
    }
}
