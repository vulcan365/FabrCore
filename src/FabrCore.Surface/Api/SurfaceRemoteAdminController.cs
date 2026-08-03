using System.Diagnostics;
using System.Reflection;
using FabrCore.Core.Auditing;
using FabrCore.Core.Blueprints;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Squads;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.Api;

/// <summary>Privileged per-principal Surface administration over the Host loopback API.</summary>
[ApiController]
[Authorize(Policy = "FabrCoreAdmin")]
[Route("fabrcoreapi/surface/admin/v1")]
public sealed class SurfaceRemoteAdminController(
    IPrincipalScopedFabrCoreStorageProvider storage,
    IOptions<SurfaceOptions> options,
    IEnumerable<IBlueprintExpander> blueprintExpanders,
    IAuditProvider? audit = null) : ControllerBase
{
    private const string Container = "surface";
    private const string PreferencesKey = "command-center/preferences";
    private const string SquadsKey = "command-center/squads";

    [HttpGet("capabilities")]
    public IActionResult GetCapabilities() => Ok(new
    {
        ApiVersion = "1",
        SurfaceVersion = typeof(SurfaceRemoteAdminController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown",
        Features = new[]
        {
            "preferences", "preferences-reset", "squad-compatibility", "squad-reset",
            "blueprint-validation", "blueprint-extension:squads"
        },
        DataScope = "cluster"
    });

    [HttpGet("principals/{principalId}/preferences")]
    public async Task<IActionResult> GetPreferences(string principalId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await storage.GetAsync<SurfacePreferences>(
                      principalId, Container, PreferencesKey, cancellationToken)
                  ?? SurfacePreferences.FromDefaults(options.Value));
    }

    [HttpPut("principals/{principalId}/preferences")]
    public async Task<IActionResult> SavePreferences(
        string principalId,
        [FromBody] SurfacePreferences preferences,
        CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        await storage.UpsertAsync(principalId, Container, PreferencesKey, preferences, cancellationToken);
        await RecordAsync(principalId, "surface/preferences/save", AuditOutcome.Success);
        return NoContent();
    }

    [HttpDelete("principals/{principalId}/preferences")]
    public async Task<IActionResult> ResetPreferences(string principalId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        await storage.DeleteAsync(principalId, Container, PreferencesKey, cancellationToken);
        await RecordAsync(principalId, "surface/preferences/reset", AuditOutcome.Success);
        return NoContent();
    }

    [HttpGet("principals/{principalId}/squads")]
    public async Task<IActionResult> GetSquads(string principalId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var state = await storage.GetAsync<SurfaceSquadAdminState>(
            principalId, Container, SquadsKey, cancellationToken);
        return Ok(state?.Squads ?? []);
    }

    [HttpPut("principals/{principalId}/squads")]
    public async Task<IActionResult> SaveSquads(
        string principalId,
        [FromBody] IReadOnlyList<SurfaceSquad> squads,
        CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        await storage.UpsertAsync(
            principalId,
            Container,
            SquadsKey,
            new SurfaceSquadAdminState { Squads = squads.ToList() },
            cancellationToken);
        await RecordAsync(principalId, "surface/squads/save", AuditOutcome.Success);
        return NoContent();
    }

    [HttpDelete("principals/{principalId}/squads")]
    public async Task<IActionResult> ResetSquads(string principalId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        await storage.DeleteAsync(principalId, Container, SquadsKey, cancellationToken);
        await RecordAsync(principalId, "surface/squads/reset", AuditOutcome.Success);
        return NoContent();
    }

    [HttpPost("principals/{principalId}/blueprints/validate")]
    public async Task<IActionResult> ValidateBlueprint(
        string principalId,
        [FromBody] FabrCoreBlueprint blueprint,
        CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var expanders = blueprintExpanders.ToDictionary(
            expander => expander.ExtensionKey,
            StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();
        foreach (var (extensionKey, extension) in blueprint.Extensions.Where(item =>
                     item.Key.StartsWith("surface", StringComparison.OrdinalIgnoreCase) ||
                     item.Key.StartsWith("squads", StringComparison.OrdinalIgnoreCase)))
        {
            if (!expanders.TryGetValue(extensionKey, out var expander))
            {
                return BadRequest(new { Error = $"Surface blueprint extension '{extensionKey}' is not installed." });
            }

            var expansion = await expander.ExpandAsync(
                new BlueprintExpansionContext { PrincipalId = principalId, Blueprint = blueprint },
                extension,
                cancellationToken);
            results.Add(new { Extension = extensionKey, AgentCount = expansion.Agents.Count });
        }

        return Ok(new { Valid = true, Extensions = results });
    }

    private IActionResult? RejectSpoofedTargetHeaders()
    {
        if (Request.Headers.ContainsKey("X-FabrCore-Admin-Target") ||
            Request.Headers.ContainsKey("x-user") ||
            Request.Headers.ContainsKey("x-user-handle"))
        {
            return BadRequest(new { Error = "Managed principals must be supplied only as encoded route parameters." });
        }

        return null;
    }

    private Task RecordAsync(string targetPrincipal, string operation, AuditOutcome outcome)
    {
        if (audit is null) return Task.CompletedTask;

        return audit.RecordAsync(new AuditEvent
        {
            Category = AuditCategory.RemoteAdministration,
            Outcome = outcome,
            SubjectPrincipal = Request.Headers["X-FabrCore-Admin-Actor"].FirstOrDefault()
                               ?? User.Identity?.Name
                               ?? "cluster-admin",
            ResourcePrincipal = targetPrincipal,
            Resource = operation,
            Permission = "remote.admin",
            TraceId = Activity.Current?.TraceId.ToString(),
            Details = new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["commandId"] = Request.Headers["X-FabrCore-Admin-Command-Id"].FirstOrDefault() ?? string.Empty
            }
        });
    }
}

public sealed class SurfaceSquadAdminState
{
    public int Version { get; set; } = 1;

    public List<SurfaceSquad> Squads { get; set; } = [];
}
