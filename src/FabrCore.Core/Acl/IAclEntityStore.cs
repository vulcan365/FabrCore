namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Async CRUD over persisted ACL entities, consumed by the management API and bootstrap.
    /// The default implementation routes writes through the single-activation
    /// <c>AclRegistryGrain</c> so index maintenance is serialized cluster-wide.
    /// </summary>
    public interface IAclEntityStore
    {
        /// <summary>Gets the full entity set with its current version.</summary>
        Task<AclSnapshotData> GetSnapshotAsync(CancellationToken cancellationToken = default);

        /// <summary>Gets the current entity-set version without transferring entities.</summary>
        Task<long> GetVersionAsync(CancellationToken cancellationToken = default);

        // ── Principals ──

        Task<AclPrincipal?> GetPrincipalAsync(string handle, CancellationToken cancellationToken = default);
        Task UpsertPrincipalAsync(AclPrincipal principal, CancellationToken cancellationToken = default);
        Task<bool> DeletePrincipalAsync(string handle, CancellationToken cancellationToken = default);

        // ── Roles ──

        Task<AclRole?> GetRoleAsync(string name, CancellationToken cancellationToken = default);
        Task UpsertRoleAsync(AclRole role, CancellationToken cancellationToken = default);
        Task<bool> DeleteRoleAsync(string name, CancellationToken cancellationToken = default);

        // ── Groups ──

        Task<AclGroup?> GetGroupAsync(string name, CancellationToken cancellationToken = default);
        Task UpsertGroupAsync(AclGroup group, CancellationToken cancellationToken = default);
        Task<bool> DeleteGroupAsync(string name, CancellationToken cancellationToken = default);
        Task AddGroupMemberAsync(string groupName, GroupMember member, CancellationToken cancellationToken = default);
        Task<bool> RemoveGroupMemberAsync(string groupName, GroupMember member, CancellationToken cancellationToken = default);

        // ── Grants ──

        Task<PermissionGrant?> GetGrantAsync(string id, CancellationToken cancellationToken = default);
        Task UpsertGrantAsync(PermissionGrant grant, CancellationToken cancellationToken = default);
        Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken = default);

        // ── Runtime configuration ──

        /// <summary>Gets the runtime enforcement-mode override, if one has been set via the API.</summary>
        Task<AclEnforcementMode?> GetEnforcementModeOverrideAsync(CancellationToken cancellationToken = default);

        /// <summary>Sets or clears (null) the runtime enforcement-mode override.</summary>
        Task SetEnforcementModeOverrideAsync(AclEnforcementMode? mode, CancellationToken cancellationToken = default);
    }
}
