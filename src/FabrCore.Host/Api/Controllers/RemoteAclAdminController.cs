using System.Diagnostics;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace FabrCore.Host.Api.Controllers;
[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1")]
[RequiresFabrCoreDatabase]
public sealed class RemoteAclAdminController(IAclEntityStore acl, IAclSnapshotProvider aclSnapshot, IAuditProvider audit) : ControllerBase
{
    [HttpPost("access/import")]
    public Task<IActionResult> Import(
        [FromBody] System.Text.Json.JsonElement document,
        [FromServices] Orleans.IClusterClient cluster,
        CancellationToken cancellationToken) => MutateAsync(null, "access/import", async () =>
        {
            var snapshot = FabrCore.Host.Database.AclMigration.Read(document);
            await cluster.GetGrain<FabrCore.Core.Interfaces.IAclRegistryGrain>(FabrCore.Core.Interfaces.IAclRegistryGrain.WellKnownKey)
                .ImportAsync(snapshot).WaitAsync(cancellationToken);
            await ((FabrCore.Host.Services.GrainBackedAclEntityStore)acl).RefreshNowAsync(cancellationToken);
            return NoContent();
        });

    private const string ActorHeader = RemoteAdminController.ActorHeader;
    private const string CommandHeader = RemoteAdminController.CommandHeader;
    [HttpGet("access")]
    public async Task<IActionResult> GetAccess(CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var snapshot = await acl.GetSnapshotAsync(cancellationToken);
        return Ok(new
        {
            snapshot.Version,
            EnforcementMode = aclSnapshot.Current.ModeOverride?.ToString(),
            snapshot.Principals,
            snapshot.Roles,
            snapshot.Groups,
            snapshot.Grants
        });
    }

    [HttpPut("access/enforcement-mode")]
    public Task<IActionResult> SetEnforcementMode(
        [FromBody] RemoteAdminEnforcementModeRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(null, "access/enforcement-mode", async () =>
        {
            await acl.SetEnforcementModeOverrideAsync(request.Mode, cancellationToken);
            return NoContent();
        });

    [HttpPut("access/principals/{handle}")]
    public Task<IActionResult> SaveAclPrincipal(
        string handle,
        [FromBody] AclPrincipal principal,
        CancellationToken cancellationToken) =>
        MutateAsync(handle, $"access/principals/{handle}/save", async () =>
        {
            principal.Handle = handle;
            await acl.UpsertPrincipalAsync(principal, cancellationToken);
            return NoContent();
        });

    [HttpDelete("access/principals/{handle}")]
    public Task<IActionResult> DeleteAclPrincipal(string handle, CancellationToken cancellationToken) =>
        MutateAsync(handle, $"access/principals/{handle}/delete", async () =>
            await acl.DeletePrincipalAsync(handle, cancellationToken) ? NoContent() : NotFound());

    [HttpPut("access/roles/{name}")]
    public Task<IActionResult> SaveAclRole(
        string name,
        [FromBody] AclRole role,
        CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/roles/{name}/save", async () =>
        {
            role.Name = name;
            await acl.UpsertRoleAsync(role, cancellationToken);
            return NoContent();
        });

    [HttpDelete("access/roles/{name}")]
    public Task<IActionResult> DeleteAclRole(string name, CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/roles/{name}/delete", async () =>
            await acl.DeleteRoleAsync(name, cancellationToken) ? NoContent() : NotFound());

    [HttpPut("access/groups/{name}")]
    public Task<IActionResult> SaveAclGroup(
        string name,
        [FromBody] AclGroup group,
        CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/groups/{name}/save", async () =>
        {
            group.Name = name;
            await acl.UpsertGroupAsync(group, cancellationToken);
            return NoContent();
        });

    [HttpDelete("access/groups/{name}")]
    public Task<IActionResult> DeleteAclGroup(string name, CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/groups/{name}/delete", async () =>
            await acl.DeleteGroupAsync(name, cancellationToken) ? NoContent() : NotFound());

    [HttpPost("access/groups/{name}/members")]
    public Task<IActionResult> AddAclGroupMember(
        string name,
        [FromBody] GroupMember member,
        CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/groups/{name}/members/add", async () =>
        {
            await acl.AddGroupMemberAsync(name, member, cancellationToken);
            return NoContent();
        });

    [HttpDelete("access/groups/{name}/members")]
    public Task<IActionResult> RemoveAclGroupMember(
        string name,
        [FromQuery] SubjectKind kind,
        [FromQuery] string handle,
        CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/groups/{name}/members/remove", async () =>
            await acl.RemoveGroupMemberAsync(name, new GroupMember(kind, handle), cancellationToken)
                ? NoContent()
                : NotFound());

    [HttpPut("access/grants/{id}")]
    public Task<IActionResult> SaveAclGrant(
        string id,
        [FromBody] PermissionGrant grant,
        CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/grants/{id}/save", async () =>
        {
            grant.Id = id;
            await acl.UpsertGrantAsync(grant, cancellationToken);
            return NoContent();
        });

    [HttpDelete("access/grants/{id}")]
    public Task<IActionResult> DeleteAclGrant(string id, CancellationToken cancellationToken) =>
        MutateAsync(null, $"access/grants/{id}/delete", async () =>
            await acl.DeleteGrantAsync(id, cancellationToken) ? NoContent() : NotFound());

    [HttpGet("access/principals/{handle}/effective")]
    public IActionResult GetEffectiveAccess(string handle)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var snapshot = aclSnapshot.Current;
        return Ok(new
        {
            Principal = handle,
            Roles = snapshot.RolesOf(handle),
            Groups = snapshot.GroupsOf(SubjectKind.Principal, handle)
                .Append(snapshot.AllPrincipalsGroup)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        });
    }

    private async Task<IActionResult> MutateAsync(
        string? targetPrincipal,
        string operation,
        Func<Task<IActionResult>> action)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;

        try
        {
            var result = await action();
            await RecordAsync(targetPrincipal, operation,
                result is ObjectResult { StatusCode: >= 400 } ? AuditOutcome.Denied : AuditOutcome.Success);
            return result;
        }
        catch
        {
            await RecordAsync(targetPrincipal, operation, AuditOutcome.Error);
            throw;
        }
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

    private Task RecordAsync(
        string? targetPrincipal,
        string operation,
        AuditOutcome outcome,
        IReadOnlyDictionary<string, string>? additionalDetails = null)
    {
        var actor = Request.Headers[ActorHeader].FirstOrDefault()
                    ?? User.Identity?.Name
                    ?? "cluster-admin";
        var details = new Dictionary<string, string>
        {
            ["operation"] = operation,
            ["commandId"] = Request.Headers[CommandHeader].FirstOrDefault() ?? string.Empty
        };
        if (additionalDetails is not null)
        {
            foreach (var (key, value) in additionalDetails)
            {
                details[key] = value;
            }
        }

        return audit.RecordAsync(new AuditEvent
        {
            Category = AuditCategory.RemoteAdministration,
            Outcome = outcome,
            SubjectPrincipal = actor,
            ResourcePrincipal = targetPrincipal,
            Resource = operation,
            Permission = "remote.admin",
            TraceId = Activity.Current?.TraceId.ToString(),
            Details = details
        });
    }

}
