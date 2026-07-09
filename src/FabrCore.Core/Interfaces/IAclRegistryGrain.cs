using FabrCore.Core.Acl;
using Orleans;

namespace FabrCore.Core.Interfaces
{
    /// <summary>
    /// Single-activation registry grain (well-known key <c>"acl"</c>) that owns all writes to
    /// persisted ACL entities. Serializing writes through one activation keeps the per-collection
    /// index documents consistent on any storage backend, without exposed ETags.
    /// Reads on the hot path never hit this grain — silos cache immutable snapshots and refresh
    /// on change notifications (stream namespace <c>AclChanged</c>) or TTL expiry.
    /// </summary>
    public interface IAclRegistryGrain : IGrainWithStringKey
    {
        /// <summary>Well-known grain key for the single activation.</summary>
        public const string WellKnownKey = "acl";

        /// <summary>
        /// Ensures built-in entities exist (System principal, dynamic groups, acl-admin role)
        /// and applies configuration seeds on first run. Idempotent.
        /// </summary>
        Task EnsureBootstrappedAsync();

        /// <summary>Gets the full entity set with its current version.</summary>
        Task<AclSnapshotData> GetSnapshotAsync();

        /// <summary>Gets the current entity-set version without transferring entities.</summary>
        Task<long> GetVersionAsync();

        // ── Principals ──

        Task<AclPrincipal?> GetPrincipalAsync(string handle);
        Task UpsertPrincipalAsync(AclPrincipal principal);
        Task<bool> DeletePrincipalAsync(string handle);

        // ── Roles ──

        Task<AclRole?> GetRoleAsync(string name);
        Task UpsertRoleAsync(AclRole role);
        Task<bool> DeleteRoleAsync(string name);

        // ── Groups ──

        Task<AclGroup?> GetGroupAsync(string name);
        Task UpsertGroupAsync(AclGroup group);
        Task<bool> DeleteGroupAsync(string name);
        Task AddGroupMemberAsync(string groupName, GroupMember member);
        Task<bool> RemoveGroupMemberAsync(string groupName, GroupMember member);

        // ── Grants ──

        Task<PermissionGrant?> GetGrantAsync(string id);
        Task UpsertGrantAsync(PermissionGrant grant);
        Task<bool> DeleteGrantAsync(string id);

        // ── Runtime configuration ──

        Task<AclEnforcementMode?> GetEnforcementModeOverrideAsync();
        Task SetEnforcementModeOverrideAsync(AclEnforcementMode? mode);
    }

    /// <summary>Published on the <c>AclChanged</c> stream namespace after every ACL mutation.</summary>
    [GenerateSerializer]
    public sealed class AclChangedNotification
    {
        [Id(0)]
        public long Version { get; set; }
    }
}
