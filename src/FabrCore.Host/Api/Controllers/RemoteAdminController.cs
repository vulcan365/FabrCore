using System.Diagnostics;
using System.Reflection;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.Blueprints;
using FabrCore.Core.Monitoring;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Configuration;
using FabrCore.Host.Security;
using FabrCore.Host.Services;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.Memory.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Api.Controllers;

/// <summary>Privileged, versioned host administration API intended for loopback use.</summary>
[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1")]
public sealed class RemoteAdminController(
    IFabrCoreAgentService agents,
    IFabrCoreBlueprintService blueprints,
    IAclEntityStore acl,
    IAclSnapshotProvider aclSnapshot,
    IAuditProvider audit,
    IAgentMessageMonitor monitor,
    IVerifiableExecutionStore evidenceStore,
    IVerifiableExecutionContext evidenceContext,
    IServiceProvider services,
    IEnumerable<IBlueprintExpander> blueprintExpanders,
    IOptions<CloudServerOptions> cloudOptions,
    IHostEnvironment environment,
    ITokenCostCalculator? tokenCosts = null) : ControllerBase
{
    public const string ActorHeader = "X-FabrCore-Admin-Actor";
    public const string CommandHeader = "X-FabrCore-Admin-Command-Id";

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;

        var cloud = cloudOptions.Value;
        return Ok(new
        {
            ApiVersion = "1",
            HostVersion = typeof(RemoteAdminController).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown",
            Environment = environment.EnvironmentName,
            ProcessId = Environment.ProcessId,
            StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            RemoteAdministration = new
            {
                cloud.RemoteAdministration.Enabled,
                LocalAdminKeyConfigured = !string.IsNullOrWhiteSpace(
                    cloud.RemoteAdministration.LocalAdminApiKey),
                cloud.RemoteAdministration.MaxBodyBytes
            }
        });
    }

    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;

        var document = new ClusterCapabilityDocument
        {
            HostVersion = typeof(RemoteAdminController).Assembly.GetName().Version?.ToString() ?? "unknown",
            MaxRequestBodyBytes = cloudOptions.Value.RemoteAdministration.MaxBodyBytes,
            BlueprintExtensions = blueprintExpanders
                .Select(expander => expander.ExtensionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList(),
            Services =
            [
                new ClusterServiceCapability
                {
                    Name = "host-admin",
                    Version = typeof(RemoteAdminController).Assembly.GetName().Version?.ToString(),
                    ApiVersion = "1",
                    Features = ["runtime", "blueprints", "acl", "audit", "monitor", "evidence", "capabilities"],
                    DataScope = "cluster",
                    MaxRequestBodyBytes = cloudOptions.Value.RemoteAdministration.MaxBodyBytes
                }
            ]
        };

        if (services.GetService<IMemoryAdminService>() is not null)
        {
            document.Services.Add(new ClusterServiceCapability
            {
                Name = "memory",
                Version = typeof(IMemoryAdminService).Assembly.GetName().Version?.ToString(),
                ApiVersion = MemoryAdminCapability.CurrentApiVersion,
                Features = ["dashboard", "scopes", "memories", "consolidation", "audit"],
                DataScope = "cluster"
            });
        }

        if (services.GetService<IGraphRagAdminService>() is not null)
        {
            document.Services.Add(new ClusterServiceCapability
            {
                Name = "graphrag",
                Version = typeof(IGraphRagAdminService).Assembly.GetName().Version?.ToString(),
                ApiVersion = GraphRagAdminCapability.CurrentApiVersion,
                Features = ["dashboard", "scopes", "documents", "graph", "search", "maintenance", "upload"],
                DataScope = "cluster",
                MaxRequestBodyBytes = cloudOptions.Value.RemoteAdministration.MaxBodyBytes
            });
        }

        return Ok(document);
    }

    [HttpGet("runtime/principals")]
    public async Task<IActionResult> GetPrincipals([FromQuery] string? status = null)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await agents.GetPrincipalsAsync(status));
    }

    [HttpGet("runtime/principals/{principalId}/agents")]
    public async Task<IActionResult> GetAgents(string principalId, [FromQuery] string? status = null)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var prefix = $"{principalId}:";
        return Ok((await agents.GetAgentsAsync(status))
            .Where(agent => agent.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [HttpGet("runtime/principals/{principalId}/agents/{agentHandle}/health")]
    public async Task<IActionResult> GetHealth(
        string principalId,
        string agentHandle,
        [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await agents.GetHealthAsync(principalId, agentHandle, detailLevel));
    }

    [HttpDelete("runtime/principals/{principalId}/agents/{agentHandle}")]
    public Task<IActionResult> EvictAgent(string principalId, string agentHandle) =>
        MutateAsync(principalId, $"runtime/agents/{agentHandle}/evict", async () =>
            Ok(await agents.EvictAgentAsync(principalId, agentHandle)));

    [HttpGet("principals/{principalId}/blueprints")]
    public async Task<IActionResult> ListBlueprints(string principalId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await blueprints.ListAsync(principalId, cancellationToken));
    }

    [HttpGet("principals/{principalId}/blueprints/{name}")]
    public async Task<IActionResult> GetBlueprint(string principalId, string name, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var blueprint = await blueprints.GetAsync(principalId, name, cancellationToken);
        return blueprint is null ? NotFound() : Ok(blueprint);
    }

    [HttpPut("principals/{principalId}/blueprints/{name}")]
    public Task<IActionResult> SaveBlueprint(
        string principalId,
        string name,
        [FromBody] FabrCoreBlueprint blueprint,
        CancellationToken cancellationToken) =>
        MutateAsync(principalId, $"blueprints/{name}/save", async () =>
        {
            blueprint.Name = name;
            await blueprints.SaveAsync(principalId, blueprint, cancellationToken);
            return NoContent();
        });

    [HttpDelete("principals/{principalId}/blueprints/{name}")]
    public Task<IActionResult> DeleteBlueprint(string principalId, string name, CancellationToken cancellationToken) =>
        MutateAsync(principalId, $"blueprints/{name}/delete", async () =>
            await blueprints.DeleteAsync(principalId, name, cancellationToken) ? NoContent() : NotFound());

    [HttpPost("principals/{principalId}/blueprints/{name}/apply")]
    public Task<IActionResult> ApplyBlueprint(
        string principalId,
        string name,
        [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
        CancellationToken cancellationToken = default) =>
        MutateAsync(principalId, $"blueprints/{name}/apply", async () =>
        {
            var blueprint = await blueprints.GetAsync(principalId, name, cancellationToken);
            return blueprint is null
                ? NotFound()
                : Ok(await blueprints.ApplyAsync(principalId, blueprint, detailLevel, cancellationToken));
        });

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

    [HttpGet("access/audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] string? targetPrincipal = null,
        [FromQuery] int limit = 100)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await audit.GetEventsAsync(new AuditQuery
        {
            SubjectPrincipal = targetPrincipal,
            Limit = Math.Clamp(limit, 1, 1000)
        }));
    }

    [HttpGet("observability/principals/{principalId}/snapshot")]
    public async Task<IActionResult> GetObservabilitySnapshot(
        string principalId,
        [FromQuery] int limit = 100)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        var effectiveLimit = Math.Clamp(limit, 1, 1000);
        var prefix = $"{principalId}:";
        static bool Owned(string? handle, string prefix) =>
            !string.IsNullOrWhiteSpace(handle) && handle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        var messages = (await monitor.GetMessagesAsync(null, 1000))
            .Where(item => Owned(item.AgentHandle, prefix)).Take(effectiveLimit).ToList();
        var events = (await monitor.GetEventsAsync(null, 1000))
            .Where(item => Owned(item.AgentHandle, prefix)).Take(effectiveLimit).ToList();
        var llmCalls = (await monitor.GetLlmCallsAsync(null, 1000))
            .Where(item => Owned(item.AgentHandle, prefix)).Take(effectiveLimit).ToList();
        var tokens = (await monitor.GetAllAgentTokenSummariesAsync())
            .Where(item => Owned(item.AgentHandle, prefix)).ToList();
        var errors = llmCalls.Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage)).ToList();
        var toolCalls = llmCalls.SelectMany(call => call.ToolCalls ?? [], (call, tool) => new
        {
            call.AgentHandle,
            call.TraceId,
            call.Timestamp,
            Tool = tool
        }).ToList();
        object? costs = null;
        if (tokenCosts is not null)
        {
            costs = llmCalls.GroupBy(call => new { call.AgentHandle, call.Model })
                .Select(group => new
                {
                    group.Key.AgentHandle,
                    group.Key.Model,
                    InputTokens = group.Sum(call => call.InputTokens),
                    OutputTokens = group.Sum(call => call.OutputTokens),
                    EstimatedUsd = tokenCosts.EstimateUsd(
                        group.Key.Model ?? "(unknown)",
                        group.Sum(call => call.InputTokens),
                        group.Sum(call => call.OutputTokens),
                        group.Sum(call => call.CachedInputTokens),
                        group.Sum(call => call.ReasoningTokens))
                }).ToList();
        }
        return Ok(new
        {
            DataScope = "silo",
            PrincipalId = principalId,
            PayloadsCaptured = monitor.LlmCaptureOptions.CapturePayloads,
            Messages = messages,
            Events = events,
            LlmCalls = llmCalls,
            Tokens = tokens,
            Errors = errors,
            ToolCalls = toolCalls,
            Costs = costs,
            PricingConfigured = tokenCosts is not null
        });
    }

    [HttpGet("observability/evidence/{traceId}/bundle")]
    public async Task<IActionResult> GetEvidenceBundle(string traceId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await evidenceStore.GetBundleAsync(traceId, cancellationToken));
    }

    [HttpPost("observability/evidence/{traceId}/verify")]
    public async Task<IActionResult> VerifyEvidence(string traceId, CancellationToken cancellationToken)
    {
        if (RejectSpoofedTargetHeaders() is { } rejected) return rejected;
        return Ok(await evidenceContext.VerifyAsync(traceId, cancellationToken));
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

    private Task RecordAsync(string? targetPrincipal, string operation, AuditOutcome outcome)
    {
        var actor = Request.Headers[ActorHeader].FirstOrDefault()
                    ?? User.Identity?.Name
                    ?? "cluster-admin";
        return audit.RecordAsync(new AuditEvent
        {
            Category = AuditCategory.RemoteAdministration,
            Outcome = outcome,
            SubjectPrincipal = actor,
            ResourcePrincipal = targetPrincipal,
            Resource = operation,
            Permission = "remote.admin",
            TraceId = Activity.Current?.TraceId.ToString(),
            Details = new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["commandId"] = Request.Headers[CommandHeader].FirstOrDefault() ?? string.Empty
            }
        });
    }
}

public sealed class RemoteAdminEnforcementModeRequest
{
    public AclEnforcementMode? Mode { get; set; }
}
