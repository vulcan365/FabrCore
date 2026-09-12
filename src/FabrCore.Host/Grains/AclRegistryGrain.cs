using FabrCore.Host.Database;
using System.Text.Json;

using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Streaming;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace FabrCore.Host.Grains
{
    /// <summary>
    /// Single-activation owner of persisted ACL entities (see <see cref="IAclRegistryGrain"/>).
    /// Entities are persisted transactionally in the SQL acl schema, independently of Orleans storage.
    /// </summary>
    public class AclRegistryGrain : Grain, IAclRegistryGrain
    {
        private readonly SqlAclRepository _repository;
        private readonly FabrCoreAclOptions _options;
        private readonly IAuditProvider _audit;
        private readonly ILogger<AclRegistryGrain> _logger;

        private readonly Dictionary<string, AclPrincipal> _principals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AclRole> _roles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AclGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PermissionGrant> _grants = new(StringComparer.OrdinalIgnoreCase);
        private long _version;
        private AclEnforcementMode? _modeOverride;
        private string _committed = "{}";

        public AclRegistryGrain(
            SqlAclRepository repository,
            IOptions<FabrCoreAclOptions> options,
            IAuditProvider audit,
            ILogger<AclRegistryGrain> logger)
        {
            _repository = repository;
            _options = options.Value;
            _audit = audit;
            _logger = logger;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (!string.Equals(this.GetPrimaryKeyString(), IAclRegistryGrain.WellKnownKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"AclRegistryGrain must be addressed with the well-known key '{IAclRegistryGrain.WellKnownKey}'.");

            Restore(await _repository.LoadAsync(cancellationToken));
            _committed = JsonSerializer.Serialize(await GetSnapshotAsync());

            _logger.LogInformation(
                "ACL registry activated: {Principals} principals, {Roles} roles, {Groups} groups, {Grants} grants (version {Version})",
                _principals.Count, _roles.Count, _groups.Count, _grants.Count, _version);

            await base.OnActivateAsync(cancellationToken);
        }

        // ── Bootstrap ──

        public async Task EnsureBootstrappedAsync()
        {
            if (_version > 0) return;

            // Built-in unrestricted System principal.
            var system = _principals.TryGetValue(_options.SystemPrincipal, out var existingSystem)
                ? existingSystem
                : new AclPrincipal { Handle = _options.SystemPrincipal, DisplayName = "System" };
            system.IsSystem = true;
            if (!system.Roles.Contains(WellKnownGroups.AclAdminRole, StringComparer.OrdinalIgnoreCase))
                system.Roles.Add(WellKnownGroups.AclAdminRole);
            await PersistPrincipalAsync(system);

            // Dynamic built-in groups — membership is computed, never stored.
            await PersistGroupAsync(new AclGroup
            {
                Name = _options.AllPrincipalsGroupId,
                Description = "All principals (dynamic membership)",
                IsDynamic = true
            });
            await PersistGroupAsync(new AclGroup
            {
                Name = _options.AllAgentsGroupId,
                Description = "All agents (dynamic membership)",
                IsDynamic = true
            });

            // Built-in acl-admin role.
            await PersistRoleAsync(new AclRole
            {
                Name = WellKnownGroups.AclAdminRole,
                Description = "Full ACL management access",
                IsBuiltIn = true,
                Grants = new List<PermissionGrant>
                {
                    new() { Id = "builtin-acl-manage", Permission = FabrPermissions.AclManageAllow, Resource = "*:*" },
                    new() { Id = "builtin-acl-read", Permission = FabrPermissions.AclReadAllow, Resource = "*:*" }
                }
            });

            // Zero-config demo affordance: everyone may message/read the System principal's agents.
            if (_grants.Count == 0 && _options.SeedDefaultSystemAgentAccess)
            {
                await PersistGrantAsync(new PermissionGrant
                {
                    Id = "builtin-system-agent-message",
                    Subject = new AclSubject(SubjectKind.Group, _options.AllPrincipalsGroupId),
                    Permission = FabrPermissions.AgentMessageAllow,
                    Resource = $"{_options.SystemPrincipal}:*"
                });
                await PersistGrantAsync(new PermissionGrant
                {
                    Id = "builtin-system-agent-read",
                    Subject = new AclSubject(SubjectKind.Group, _options.AllPrincipalsGroupId),
                    Permission = FabrPermissions.AgentReadAllow,
                    Resource = $"{_options.SystemPrincipal}:*"
                });
                _logger.LogInformation("Seeded default grants: all principals may message/read '{System}:*' agents",
                    _options.SystemPrincipal);
            }

            await CommitAsync();

            RecordAudit(new AuditEvent
            {
                Category = AuditCategory.Bootstrap,
                Outcome = AuditOutcome.Success,
                SubjectPrincipal = _options.SystemPrincipal,
                Resource = "acl",
                EnforcementMode = (_modeOverride ?? _options.Mode).ToString(),
                Reason = "ACL registry bootstrapped",
                Details =
                {
                    ["principals"] = _principals.Count.ToString(),
                    ["roles"] = _roles.Count.ToString(),
                    ["groups"] = _groups.Count.ToString(),
                    ["grants"] = _grants.Count.ToString()
                }
            });
        }

        // ── Snapshot / version ──

        public Task<AclSnapshotData> GetSnapshotAsync()
            => Task.FromResult(new AclSnapshotData
            {
                Version = _version,
                ModeOverride = _modeOverride,
                Principals = _principals.Values.ToList(),
                Roles = _roles.Values.ToList(),
                Groups = _groups.Values.ToList(),
                Grants = _grants.Values.ToList()
            });

        public async Task<long> GetVersionAsync()
        {
            if (await _repository.GetVersionAsync() != _version)
            {
                Restore(await _repository.LoadAsync());
                _committed = JsonSerializer.Serialize(await GetSnapshotAsync());
            }
            return _version;
        }

        // ── Principals ──

        public async Task ImportAsync(AclSnapshotData snapshot)
        {
            var merged = AclMigration.MergeIntoEmpty(await GetSnapshotAsync(), snapshot, _options);
            Restore(merged);
            await CommitAsync();
        }

        public Task<AclPrincipal?> GetPrincipalAsync(string handle)
            => Task.FromResult(_principals.TryGetValue(handle, out var principal) ? principal : null);

        public async Task UpsertPrincipalAsync(AclPrincipal principal)
        {
            if (string.IsNullOrWhiteSpace(principal.Handle))
                throw new ArgumentException("Principal handle is required.");

            // The System principal's flag is owned by bootstrap; don't let API writes strip or add it.
            principal.IsSystem = _principals.TryGetValue(principal.Handle, out var existing) && existing.IsSystem;

            await PersistPrincipalAsync(principal);
            await CommitAsync();
        }

        public async Task<bool> DeletePrincipalAsync(string handle)
        {
            if (!_principals.TryGetValue(handle, out var principal))
                return false;

            if (principal.IsSystem)
                throw new InvalidOperationException("The built-in System principal cannot be deleted.");

            _principals.Remove(handle);
            await CommitAsync();
            return true;
        }

        // ── Roles ──

        public Task<AclRole?> GetRoleAsync(string name)
            => Task.FromResult(_roles.TryGetValue(name, out var role) ? role : null);

        public async Task UpsertRoleAsync(AclRole role)
        {
            if (string.IsNullOrWhiteSpace(role.Name))
                throw new ArgumentException("Role name is required.");

            foreach (var grant in role.Grants)
                PermissionName.Parse(grant.Permission); // validate; throws FormatException

            role.IsBuiltIn = _roles.TryGetValue(role.Name, out var existing) && existing.IsBuiltIn;

            await PersistRoleAsync(role);
            await CommitAsync();
        }

        public async Task<bool> DeleteRoleAsync(string name)
        {
            if (!_roles.TryGetValue(name, out var role))
                return false;

            if (role.IsBuiltIn)
                throw new InvalidOperationException($"Built-in role '{role.Name}' cannot be deleted.");

            _roles.Remove(name);
            await CommitAsync();
            return true;
        }

        // ── Groups ──

        public Task<AclGroup?> GetGroupAsync(string name)
            => Task.FromResult(_groups.TryGetValue(name, out var group) ? group : null);

        public async Task UpsertGroupAsync(AclGroup group)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                throw new ArgumentException("Group name is required.");

            if (_groups.TryGetValue(group.Name, out var existing) && existing.IsDynamic)
            {
                if (group.Members.Count > 0)
                    throw new InvalidOperationException(
                        $"Group '{group.Name}' is dynamic — its membership is computed and cannot be edited.");
                group.IsDynamic = true;
            }
            else if (group.IsDynamic)
            {
                throw new InvalidOperationException("Dynamic groups are built-in and cannot be created via the API.");
            }

            await PersistGroupAsync(group);
            await CommitAsync();
        }

        public async Task<bool> DeleteGroupAsync(string name)
        {
            if (!_groups.TryGetValue(name, out var group))
                return false;

            if (group.IsDynamic)
                throw new InvalidOperationException($"Built-in dynamic group '{group.Name}' cannot be deleted.");

            _groups.Remove(name);
            await CommitAsync();
            return true;
        }

        public async Task AddGroupMemberAsync(string groupName, GroupMember member)
        {
            if (!_groups.TryGetValue(groupName, out var group))
                throw new ArgumentException($"Group '{groupName}' does not exist.");

            if (group.IsDynamic)
                throw new InvalidOperationException(
                    $"Group '{group.Name}' is dynamic — its membership is computed and cannot be edited.");

            if (string.IsNullOrWhiteSpace(member.Handle))
                throw new ArgumentException("Member handle is required.");

            if (!group.Members.Any(m => m.Kind == member.Kind &&
                    string.Equals(m.Handle, member.Handle, StringComparison.OrdinalIgnoreCase)))
            {
                group.Members.Add(member);
                await PersistGroupAsync(group);
                await CommitAsync();
            }
        }

        public async Task<bool> RemoveGroupMemberAsync(string groupName, GroupMember member)
        {
            if (!_groups.TryGetValue(groupName, out var group))
                return false;

            if (group.IsDynamic)
                throw new InvalidOperationException(
                    $"Group '{group.Name}' is dynamic — its membership is computed and cannot be edited.");

            var removed = group.Members.RemoveAll(m => m.Kind == member.Kind &&
                string.Equals(m.Handle, member.Handle, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await PersistGroupAsync(group);
                await CommitAsync();
            }

            return removed;
        }

        // ── Grants ──

        public Task<PermissionGrant?> GetGrantAsync(string id)
            => Task.FromResult(_grants.TryGetValue(id, out var grant) ? grant : null);

        public async Task UpsertGrantAsync(PermissionGrant grant)
        {
            if (string.IsNullOrWhiteSpace(grant.Id))
                grant.Id = Guid.NewGuid().ToString("n");

            if (grant.Subject is null || string.IsNullOrWhiteSpace(grant.Subject.Selector))
                throw new ArgumentException("Grant subject is required.");

            if (string.IsNullOrWhiteSpace(grant.Resource))
                grant.Resource = "*:*";

            PermissionName.Parse(grant.Permission); // validate; throws FormatException

            await PersistGrantAsync(grant);
            await CommitAsync();
        }

        public async Task<bool> DeleteGrantAsync(string id)
        {
            if (!_grants.Remove(id))
                return false;

            await CommitAsync();
            return true;
        }

        // ── Runtime configuration ──

        public Task<AclEnforcementMode?> GetEnforcementModeOverrideAsync()
            => Task.FromResult(_modeOverride);

        public async Task SetEnforcementModeOverrideAsync(AclEnforcementMode? mode)
        {
            _modeOverride = mode;
            await CommitAsync();
        }

        // ── Persistence helpers ──

        private Task PersistPrincipalAsync(AclPrincipal principal) { _principals[principal.Handle] = principal; return Task.CompletedTask; }
        private Task PersistRoleAsync(AclRole role) { _roles[role.Name] = role; return Task.CompletedTask; }
        private Task PersistGroupAsync(AclGroup group) { _groups[group.Name] = group; return Task.CompletedTask; }
        private Task PersistGrantAsync(PermissionGrant grant) { _grants[grant.Id] = grant; return Task.CompletedTask; }

        private void Restore(AclSnapshotData data)
        {
            _principals.Clear(); foreach (var p in data.Principals) _principals.Add(p.Handle,p);
            _roles.Clear(); foreach (var r in data.Roles) _roles.Add(r.Name,r);
            _groups.Clear(); foreach (var g in data.Groups) _groups.Add(g.Name,g);
            _grants.Clear(); foreach (var g in data.Grants) _grants.Add(g.Id,g);
            _version=data.Version; _modeOverride=data.ModeOverride;
        }

        private async Task CommitAsync()
        {
            try
            {
                await _repository.SaveAsync(await GetSnapshotAsync(), _version);
                _version++;
                _committed = JsonSerializer.Serialize(await GetSnapshotAsync());
            }
            catch
            {
                // Never serve an in-memory mutation that failed to commit.
                Restore(JsonSerializer.Deserialize<AclSnapshotData>(_committed)!);
                try { Restore(await _repository.LoadAsync()); _committed = JsonSerializer.Serialize(await GetSnapshotAsync()); }
                catch { DeactivateOnIdle(); }
                throw;
            }

            try
            {
                var streamProvider = this.GetStreamProvider(StreamConstants.ProviderName);
                var stream = streamProvider.GetStream<AclChangedNotification>(
                    StreamId.Create(StreamConstants.AclChangedNamespace, IAclRegistryGrain.WellKnownKey));
                await stream.OnNextAsync(new AclChangedNotification { Version = _version });
            }
            catch (Exception ex)
            {
                // Change notification is best-effort — silo caches fall back to TTL refresh.
                _logger.LogDebug(ex, "Failed to publish ACL change notification for version {Version}", _version);
            }
        }

        private void RecordAudit(AuditEvent auditEvent)
        {
            try
            {
                var task = _audit.RecordAsync(auditEvent);
                if (!task.IsCompletedSuccessfully)
                {
                    task.ContinueWith(
                        t => _logger.LogWarning(t.Exception, "Audit provider failed to record event {EventId}", auditEvent.Id),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit provider failed to record event {EventId}", auditEvent.Id);
            }
        }

    }
}
