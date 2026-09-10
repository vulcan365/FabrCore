using System.Security.Cryptography;
using System.Text;
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
    /// Entities are persisted through <see cref="IUserScopedFabrCoreStorageProvider"/> — the same
    /// abstraction behind the Storage API — under the <c>system</c> scope in <c>fabrcore-acl-*</c>
    /// containers. Direct writes to those containers bypass index maintenance and cache
    /// invalidation and are unsupported.
    /// </summary>
    public class AclRegistryGrain : Grain, IAclRegistryGrain
    {
        private const string StorageUserHandle = "system";
        private const string PrincipalsContainer = "fabrcore-acl-principals";
        private const string RolesContainer = "fabrcore-acl-roles";
        private const string GroupsContainer = "fabrcore-acl-groups";
        private const string GrantsContainer = "fabrcore-acl-grants";
        private const string MetaContainer = "fabrcore-acl-meta";
        private const string ConfigKey = "config";
        private const string BootstrapKey = "bootstrap";
        private const int CurrentSchemaVersion = 1;

        private readonly IUserScopedFabrCoreStorageProvider _storage;
        private readonly FabrCoreAclOptions _options;
        private readonly IAuditProvider _audit;
        private readonly ILogger<AclRegistryGrain> _logger;

        private readonly Dictionary<string, AclPrincipal> _principals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AclRole> _roles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AclGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PermissionGrant> _grants = new(StringComparer.OrdinalIgnoreCase);
        private long _version;
        private AclEnforcementMode? _modeOverride;

        public AclRegistryGrain(
            IUserScopedFabrCoreStorageProvider storage,
            IOptions<FabrCoreAclOptions> options,
            IAuditProvider audit,
            ILogger<AclRegistryGrain> logger)
        {
            _storage = storage;
            _options = options.Value;
            _audit = audit;
            _logger = logger;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (!string.Equals(this.GetPrimaryKeyString(), IAclRegistryGrain.WellKnownKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"AclRegistryGrain must be addressed with the well-known key '{IAclRegistryGrain.WellKnownKey}'.");

            var config = await _storage.GetAsync<AclConfigDocument>(StorageUserHandle, MetaContainer, ConfigKey, cancellationToken);
            _version = config?.Version ?? 0;
            _modeOverride = config?.ModeOverride;

            await LoadCollectionAsync("principals", PrincipalsContainer, _principals,
                (AclPrincipal p) => p.Handle, cancellationToken);
            await LoadCollectionAsync("roles", RolesContainer, _roles,
                (AclRole r) => r.Name, cancellationToken);
            await LoadCollectionAsync("groups", GroupsContainer, _groups,
                (AclGroup g) => g.Name, cancellationToken);
            await LoadCollectionAsync("grants", GrantsContainer, _grants,
                (PermissionGrant g) => g.Id, cancellationToken);

            _logger.LogInformation(
                "ACL registry activated: {Principals} principals, {Roles} roles, {Groups} groups, {Grants} grants (version {Version})",
                _principals.Count, _roles.Count, _groups.Count, _grants.Count, _version);

            await base.OnActivateAsync(cancellationToken);
        }

        // ── Bootstrap ──

        public async Task EnsureBootstrappedAsync()
        {
            var seedHash = ComputeSeedHash();
            var marker = await _storage.GetAsync<AclBootstrapMarker>(StorageUserHandle, MetaContainer, BootstrapKey);
            if (marker is not null && marker.SchemaVersion == CurrentSchemaVersion)
            {
                if (!string.Equals(marker.SeedHash, seedHash, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "ACL seed configuration changed since first bootstrap. Seeds apply on first run only — " +
                        "use the ACL management API to change entities on a bootstrapped system.");
                }
                return;
            }

            _logger.LogInformation("Bootstrapping ACL registry (schema {SchemaVersion})", CurrentSchemaVersion);

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

            await ApplySeedsAsync();

            // Marker written last so a crash mid-bootstrap re-runs cleanly (all writes are idempotent upserts).
            await _storage.UpsertAsync(StorageUserHandle, MetaContainer, BootstrapKey, new AclBootstrapMarker
            {
                SchemaVersion = CurrentSchemaVersion,
                BootstrappedUtc = DateTime.UtcNow,
                SystemPrincipal = _options.SystemPrincipal,
                SeedHash = seedHash
            });

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

        public Task<long> GetVersionAsync() => Task.FromResult(_version);

        // ── Principals ──

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
            await _storage.DeleteAsync(StorageUserHandle, PrincipalsContainer, NormalizeKey(handle));
            await WriteIndexAsync(PrincipalsContainer, _principals.Keys);
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
            await _storage.DeleteAsync(StorageUserHandle, RolesContainer, NormalizeKey(name));
            await WriteIndexAsync(RolesContainer, _roles.Keys);
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
            await _storage.DeleteAsync(StorageUserHandle, GroupsContainer, NormalizeKey(name));
            await WriteIndexAsync(GroupsContainer, _groups.Keys);
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

            await _storage.DeleteAsync(StorageUserHandle, GrantsContainer, NormalizeKey(id));
            await WriteIndexAsync(GrantsContainer, _grants.Keys);
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

        private async Task PersistPrincipalAsync(AclPrincipal principal)
        {
            _principals[principal.Handle] = principal;
            await _storage.UpsertAsync(StorageUserHandle, PrincipalsContainer, NormalizeKey(principal.Handle), principal);
            await WriteIndexAsync(PrincipalsContainer, _principals.Keys);
        }

        private async Task PersistRoleAsync(AclRole role)
        {
            _roles[role.Name] = role;
            await _storage.UpsertAsync(StorageUserHandle, RolesContainer, NormalizeKey(role.Name), role);
            await WriteIndexAsync(RolesContainer, _roles.Keys);
        }

        private async Task PersistGroupAsync(AclGroup group)
        {
            _groups[group.Name] = group;
            await _storage.UpsertAsync(StorageUserHandle, GroupsContainer, NormalizeKey(group.Name), group);
            await WriteIndexAsync(GroupsContainer, _groups.Keys);
        }

        private async Task PersistGrantAsync(PermissionGrant grant)
        {
            _grants[grant.Id] = grant;
            await _storage.UpsertAsync(StorageUserHandle, GrantsContainer, NormalizeKey(grant.Id), grant);
            await WriteIndexAsync(GrantsContainer, _grants.Keys);
        }

        private Task WriteIndexAsync(string container, IEnumerable<string> keys)
            => _storage.UpsertAsync(StorageUserHandle, MetaContainer, $"index/{container}",
                new AclIndexDocument { Keys = keys.ToList() });

        /// <summary>Bumps the version, persists config, publishes the change notification.</summary>
        private async Task CommitAsync()
        {
            _version++;
            await _storage.UpsertAsync(StorageUserHandle, MetaContainer, ConfigKey, new AclConfigDocument
            {
                Version = _version,
                ModeOverride = _modeOverride
            });

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

        private async Task LoadCollectionAsync<T>(
            string label,
            string container,
            Dictionary<string, T> target,
            Func<T, string> keyOf,
            CancellationToken cancellationToken)
        {
            var index = await _storage.GetAsync<AclIndexDocument>(
                StorageUserHandle, MetaContainer, $"index/{container}", cancellationToken);
            if (index is null)
                return;

            var missing = 0;
            foreach (var key in index.Keys)
            {
                var entity = await _storage.GetAsync<T>(StorageUserHandle, container, NormalizeKey(key), cancellationToken);
                if (entity is null)
                {
                    missing++; // self-heal: tolerate index entries whose entity is gone
                    continue;
                }
                target[keyOf(entity)] = entity;
            }

            if (missing > 0)
            {
                _logger.LogWarning("ACL {Label} index referenced {Missing} missing entities — index will self-heal on next write",
                    label, missing);
            }
        }

        private async Task ApplySeedsAsync()
        {
            var seed = _options.Seed;
            if (seed is null)
                return;

            foreach (var principalSeed in seed.Principals)
            {
                if (string.IsNullOrWhiteSpace(principalSeed.Handle))
                    continue;
                await PersistPrincipalAsync(new AclPrincipal
                {
                    Handle = principalSeed.Handle,
                    DisplayName = principalSeed.DisplayName,
                    Roles = new List<string>(principalSeed.Roles)
                });
            }

            foreach (var roleSeed in seed.Roles)
            {
                if (string.IsNullOrWhiteSpace(roleSeed.Name))
                    continue;
                await PersistRoleAsync(new AclRole
                {
                    Name = roleSeed.Name,
                    Description = roleSeed.Description,
                    Grants = roleSeed.Grants.Select(g => new PermissionGrant
                    {
                        Permission = PermissionName.Parse(g.Permission).Value,
                        Resource = string.IsNullOrWhiteSpace(g.Resource) ? "*:*" : g.Resource
                    }).ToList()
                });
            }

            foreach (var groupSeed in seed.Groups)
            {
                if (string.IsNullOrWhiteSpace(groupSeed.Name))
                    continue;
                await PersistGroupAsync(new AclGroup
                {
                    Name = groupSeed.Name,
                    Description = groupSeed.Description,
                    Roles = new List<string>(groupSeed.Roles),
                    Members = groupSeed.Members.Select(ParseMember).ToList()
                });
            }

            foreach (var grantSeed in seed.Grants)
            {
                await PersistGrantAsync(new PermissionGrant
                {
                    Subject = AclSubject.Parse(grantSeed.Subject),
                    Permission = PermissionName.Parse(grantSeed.Permission).Value,
                    Resource = string.IsNullOrWhiteSpace(grantSeed.Resource) ? "*:*" : grantSeed.Resource
                });
            }

            _logger.LogInformation(
                "Applied ACL seeds: {Principals} principals, {Roles} roles, {Groups} groups, {Grants} grants",
                seed.Principals.Count, seed.Roles.Count, seed.Groups.Count, seed.Grants.Count);
        }

        private static GroupMember ParseMember(string member)
        {
            var subject = AclSubject.Parse(member);
            if (subject.Kind is not (SubjectKind.Principal or SubjectKind.Agent))
                throw new FormatException(
                    $"Invalid group member '{member}'. Members must be 'principal:<handle>' or 'agent:<principal>:<agent>'.");
            return new GroupMember(subject.Kind, subject.Selector);
        }

        private string ComputeSeedHash()
        {
            var json = JsonSerializer.Serialize(_options.Seed);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        private static string NormalizeKey(string key) => key.ToLowerInvariant();

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

        private sealed class AclIndexDocument
        {
            public List<string> Keys { get; set; } = new();
        }

        private sealed class AclConfigDocument
        {
            public long Version { get; set; }
            public AclEnforcementMode? ModeOverride { get; set; }
        }

        private sealed class AclBootstrapMarker
        {
            public int SchemaVersion { get; set; }
            public DateTime BootstrappedUtc { get; set; }
            public string SystemPrincipal { get; set; } = string.Empty;
            public string? SeedHash { get; set; }
        }
    }
}
