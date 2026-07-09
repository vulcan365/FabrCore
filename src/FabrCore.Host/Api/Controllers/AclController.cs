using System.Diagnostics;

using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Management API for the ACL: principals, roles, groups, and permission grants, plus
    /// evaluate/check queries and runtime configuration.
    /// <para>
    /// Authorization: the caller identity is the <c>x-user-handle</c> header (authN is the
    /// hosting layer's responsibility). Mutations require <c>acl.manage.allow</c>; reads and
    /// evaluate/check require <c>acl.read.allow</c> or <c>acl.manage.allow</c>. The System
    /// principal bypasses all checks. Every call — success or denial — emits an
    /// <see cref="AuditCategory.AclManagement"/> audit event.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("fabrcoreapi/acl")]
    public class AclController : ControllerBase
    {
        private readonly IAclEntityStore _store;
        private readonly IAclSnapshotProvider _snapshotProvider;
        private readonly IAclEvaluator _evaluator;
        private readonly IAuditProvider _audit;
        private readonly FabrCoreAclOptions _options;
        private readonly ILogger<AclController> _logger;

        public AclController(
            IAclEntityStore store,
            IAclSnapshotProvider snapshotProvider,
            IAclEvaluator evaluator,
            IAuditProvider audit,
            IOptions<FabrCoreAclOptions> options,
            ILogger<AclController> logger)
        {
            _store = store;
            _snapshotProvider = snapshotProvider;
            _evaluator = evaluator;
            _audit = audit;
            _options = options.Value;
            _logger = logger;
        }

        // ── Principals ──

        [HttpGet("principals")]
        public async Task<IActionResult> GetPrincipals(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, "principals") is { } denied) return denied;
            var snapshot = await _store.GetSnapshotAsync(cancellationToken);
            return Ok(snapshot.Principals);
        }

        [HttpGet("principals/{handle}")]
        public async Task<IActionResult> GetPrincipal(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, $"principals/{handle}") is { } denied) return denied;
            var principal = await _store.GetPrincipalAsync(handle, cancellationToken);
            return principal is null ? NotFound() : Ok(principal);
        }

        [HttpPut("principals/{handle}")]
        public Task<IActionResult> UpsertPrincipal(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle,
            [FromBody] AclPrincipal principal,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "upsert", $"principals/{handle}", async () =>
            {
                principal.Handle = handle;
                await _store.UpsertPrincipalAsync(principal, cancellationToken);
                return NoContent();
            });

        [HttpDelete("principals/{handle}")]
        public Task<IActionResult> DeletePrincipal(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "delete", $"principals/{handle}", async () =>
                await _store.DeletePrincipalAsync(handle, cancellationToken) ? NoContent() : NotFound());

        // ── Effective memberships (addon query surface) ──

        [HttpGet("principals/{handle}/roles")]
        public IActionResult GetPrincipalRoles(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle)
        {
            if (AuthorizeRead(userHandle, $"principals/{handle}/roles") is { } denied) return denied;
            return Ok(_snapshotProvider.Current.RolesOf(handle));
        }

        [HttpGet("principals/{handle}/groups")]
        public IActionResult GetPrincipalGroups(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle)
        {
            if (AuthorizeRead(userHandle, $"principals/{handle}/groups") is { } denied) return denied;

            var snapshot = _snapshotProvider.Current;
            var groups = new List<string>(snapshot.GroupsOf(SubjectKind.Principal, handle))
            {
                snapshot.AllPrincipalsGroup // dynamic membership: every principal
            };
            return Ok(groups);
        }

        // ── Roles ──

        [HttpGet("roles")]
        public async Task<IActionResult> GetRoles(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, "roles") is { } denied) return denied;
            var snapshot = await _store.GetSnapshotAsync(cancellationToken);
            return Ok(snapshot.Roles);
        }

        [HttpGet("roles/{name}")]
        public async Task<IActionResult> GetRole(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, $"roles/{name}") is { } denied) return denied;
            var role = await _store.GetRoleAsync(name, cancellationToken);
            return role is null ? NotFound() : Ok(role);
        }

        [HttpPut("roles/{name}")]
        public Task<IActionResult> UpsertRole(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            [FromBody] AclRole role,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "upsert", $"roles/{name}", async () =>
            {
                role.Name = name;
                await _store.UpsertRoleAsync(role, cancellationToken);
                return NoContent();
            });

        [HttpDelete("roles/{name}")]
        public Task<IActionResult> DeleteRole(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "delete", $"roles/{name}", async () =>
                await _store.DeleteRoleAsync(name, cancellationToken) ? NoContent() : NotFound());

        // ── Groups ──

        [HttpGet("groups")]
        public async Task<IActionResult> GetGroups(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, "groups") is { } denied) return denied;
            var snapshot = await _store.GetSnapshotAsync(cancellationToken);
            return Ok(snapshot.Groups);
        }

        [HttpGet("groups/{name}")]
        public async Task<IActionResult> GetGroup(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, $"groups/{name}") is { } denied) return denied;
            var group = await _store.GetGroupAsync(name, cancellationToken);
            return group is null ? NotFound() : Ok(group);
        }

        [HttpPut("groups/{name}")]
        public Task<IActionResult> UpsertGroup(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            [FromBody] AclGroup group,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "upsert", $"groups/{name}", async () =>
            {
                group.Name = name;
                await _store.UpsertGroupAsync(group, cancellationToken);
                return NoContent();
            });

        [HttpDelete("groups/{name}")]
        public Task<IActionResult> DeleteGroup(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "delete", $"groups/{name}", async () =>
                await _store.DeleteGroupAsync(name, cancellationToken) ? NoContent() : NotFound());

        [HttpPost("groups/{name}/members")]
        public Task<IActionResult> AddGroupMember(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            [FromBody] GroupMember member,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "add-member", $"groups/{name}", async () =>
            {
                await _store.AddGroupMemberAsync(name, member, cancellationToken);
                return NoContent();
            });

        [HttpDelete("groups/{name}/members")]
        public Task<IActionResult> RemoveGroupMember(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string name,
            [FromQuery] SubjectKind kind,
            [FromQuery] string handle,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "remove-member", $"groups/{name}", async () =>
                await _store.RemoveGroupMemberAsync(name, new GroupMember(kind, handle), cancellationToken)
                    ? NoContent()
                    : NotFound());

        // ── Grants ──

        [HttpGet("grants")]
        public async Task<IActionResult> GetGrants(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, "grants") is { } denied) return denied;
            var snapshot = await _store.GetSnapshotAsync(cancellationToken);
            return Ok(snapshot.Grants);
        }

        [HttpGet("grants/{id}")]
        public async Task<IActionResult> GetGrant(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string id,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, $"grants/{id}") is { } denied) return denied;
            var grant = await _store.GetGrantAsync(id, cancellationToken);
            return grant is null ? NotFound() : Ok(grant);
        }

        [HttpPut("grants/{id}")]
        public Task<IActionResult> UpsertGrant(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string id,
            [FromBody] PermissionGrant grant,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "upsert", $"grants/{id}", async () =>
            {
                grant.Id = id;
                await _store.UpsertGrantAsync(grant, cancellationToken);
                return NoContent();
            });

        [HttpDelete("grants/{id}")]
        public Task<IActionResult> DeleteGrant(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string id,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "delete", $"grants/{id}", async () =>
                await _store.DeleteGrantAsync(id, cancellationToken) ? NoContent() : NotFound());

        // ── Evaluate / check ──

        [HttpPost("evaluate")]
        public IActionResult Evaluate(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] AclEvaluationRequest request)
        {
            if (AuthorizeRead(userHandle, "evaluate") is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(request.SubjectPrincipal))
                return BadRequest("SubjectPrincipal is required.");

            AclAction action;
            try
            {
                action = AclAction.Parse(request.Action);
            }
            catch (FormatException ex)
            {
                return BadRequest(ex.Message);
            }

            var decision = _evaluator.Evaluate(
                new AclSubjectContext(request.SubjectPrincipal, request.SubjectAgent),
                action,
                string.IsNullOrWhiteSpace(request.Resource) ? "*:*" : request.Resource);

            RecordManagementAudit(userHandle, "evaluate",
                $"{request.SubjectPrincipal} -> {request.Action} on {request.Resource}", AuditOutcome.Success);

            return Ok(ToResponse(decision));
        }

        [HttpPost("check")]
        public IActionResult Check(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] AclCheckRequest request)
        {
            if (AuthorizeRead(userHandle, "check") is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(request.Principal))
                return BadRequest("Principal is required.");

            AclAction action;
            try
            {
                action = AclAction.Parse(request.Action);
            }
            catch (FormatException ex)
            {
                return BadRequest(ex.Message);
            }

            var decision = _evaluator.Evaluate(
                new AclSubjectContext(request.Principal, null),
                action,
                string.IsNullOrWhiteSpace(request.Resource) ? "*:*" : request.Resource!);

            return Ok(ToResponse(decision));
        }

        // ── Configuration ──

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            CancellationToken cancellationToken)
        {
            if (AuthorizeRead(userHandle, "config") is { } denied) return denied;

            var modeOverride = await _store.GetEnforcementModeOverrideAsync(cancellationToken);
            return Ok(new AclConfigResponse
            {
                SystemPrincipal = _options.SystemPrincipal,
                ConfiguredMode = _options.Mode.ToString(),
                ModeOverride = modeOverride?.ToString(),
                EffectiveMode = (modeOverride ?? _options.Mode).ToString(),
                AllPrincipalsGroupId = _options.AllPrincipalsGroupId,
                AllAgentsGroupId = _options.AllAgentsGroupId,
                SnapshotVersion = _snapshotProvider.Current.Version
            });
        }

        [HttpPut("config/enforcement-mode")]
        public Task<IActionResult> SetEnforcementMode(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] SetEnforcementModeRequest request,
            CancellationToken cancellationToken)
            => ExecuteManageAsync(userHandle, "set-enforcement-mode", $"config ({request.Mode ?? "clear override"})", async () =>
            {
                AclEnforcementMode? mode = null;
                if (!string.IsNullOrWhiteSpace(request.Mode))
                {
                    if (!Enum.TryParse<AclEnforcementMode>(request.Mode, ignoreCase: true, out var parsed))
                        return BadRequest($"Invalid enforcement mode '{request.Mode}'. Expected Disabled, AuditOnly, or Enforce.");
                    mode = parsed;
                }

                await _store.SetEnforcementModeOverrideAsync(mode, cancellationToken);
                return NoContent();
            });

        // ── Helpers ──

        /// <summary>Authorizes a read-only call: acl.read or acl.manage (System bypasses).</summary>
        private IActionResult? AuthorizeRead(string userHandle, string resource)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
                return BadRequest("x-user-handle header is required.");

            if (_evaluator.CanReadAcl(userHandle).IsAllowed || _evaluator.CanManageAcl(userHandle).IsAllowed)
                return null;

            RecordManagementAudit(userHandle, "read", resource, AuditOutcome.Denied);
            return StatusCode(403, new { Error = $"Access denied: '{userHandle}' cannot read ACL data." });
        }

        /// <summary>
        /// Authorizes and runs a mutation with acl.manage (System bypasses), mapping domain
        /// exceptions to HTTP: InvalidOperationException → 409 (built-in protections),
        /// Argument/FormatException → 400.
        /// </summary>
        private async Task<IActionResult> ExecuteManageAsync(
            string userHandle, string operation, string resource, Func<Task<IActionResult>> action)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
                return BadRequest("x-user-handle header is required.");

            if (!_evaluator.CanManageAcl(userHandle).IsAllowed)
            {
                RecordManagementAudit(userHandle, operation, resource, AuditOutcome.Denied);
                return StatusCode(403, new { Error = $"Access denied: '{userHandle}' cannot manage ACL data." });
            }

            try
            {
                var result = await action();
                RecordManagementAudit(userHandle, operation, resource, AuditOutcome.Success);
                return result;
            }
            catch (InvalidOperationException ex)
            {
                RecordManagementAudit(userHandle, operation, resource, AuditOutcome.Error, ex.Message);
                return Conflict(new { Error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                RecordManagementAudit(userHandle, operation, resource, AuditOutcome.Error, ex.Message);
                return BadRequest(new { Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ACL management operation failed: {Operation} {Resource}", operation, resource);
                RecordManagementAudit(userHandle, operation, resource, AuditOutcome.Error, ex.Message);
                return StatusCode(500, "ACL management operation failed.");
            }
        }

        private void RecordManagementAudit(
            string userHandle, string operation, string resource, AuditOutcome outcome, string? reason = null)
        {
            try
            {
                _audit.RecordAsync(new AuditEvent
                {
                    Category = AuditCategory.AclManagement,
                    Outcome = outcome,
                    SubjectPrincipal = userHandle,
                    Resource = $"acl/{resource}",
                    Permission = FabrActions.AclManage.ToString(),
                    EnforcementMode = _evaluator.Mode.ToString(),
                    WasEnforced = outcome == AuditOutcome.Denied,
                    Reason = reason ?? operation,
                    TraceId = Activity.Current?.TraceId.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record ACL management audit event");
            }
        }

        private static AclEvaluationResponse ToResponse(AclDecision decision) => new()
        {
            Allowed = decision.IsAllowed,
            Outcome = decision.Outcome.ToString(),
            Mode = decision.Mode.ToString(),
            Reason = decision.Reason,
            DecidingGrant = decision.DecidingGrant
        };
    }
}
