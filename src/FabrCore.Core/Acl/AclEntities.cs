using Orleans;

namespace FabrCore.Core.Acl
{
    /// <summary>Kind of subject a grant or group member refers to.</summary>
    public enum SubjectKind
    {
        /// <summary>A principal, identified by its principal handle (e.g. <c>"alice"</c>).</summary>
        Principal,

        /// <summary>A specific agent, identified by its full handle (e.g. <c>"alice:agent1"</c>).</summary>
        Agent,

        /// <summary>A role, identified by role name. Valid for grant subjects only.</summary>
        Role,

        /// <summary>A group, identified by group name. Valid for grant subjects only.</summary>
        Group
    }

    /// <summary>
    /// A principal known to the ACL system. The handle is the same string that keys
    /// the principal's <c>PrincipalGrain</c>.
    /// </summary>
    [GenerateSerializer]
    public sealed class AclPrincipal
    {
        /// <summary>Principal handle (case-insensitive unique id).</summary>
        [Id(0)]
        public string Handle { get; set; } = string.Empty;

        /// <summary>Optional display name.</summary>
        [Id(1)]
        public string? DisplayName { get; set; }

        /// <summary>Names of roles assigned directly to this principal.</summary>
        [Id(2)]
        public List<string> Roles { get; set; } = new();

        /// <summary>True for the built-in unrestricted System principal. Set by bootstrap; not editable.</summary>
        [Id(3)]
        public bool IsSystem { get; set; }
    }

    /// <summary>
    /// A named set of permission grants. Assign a role to principals directly
    /// (<see cref="AclPrincipal.Roles"/>) or to every member of a group (<see cref="AclGroup.Roles"/>).
    /// </summary>
    [GenerateSerializer]
    public sealed class AclRole
    {
        /// <summary>Role name (case-insensitive unique id).</summary>
        [Id(0)]
        public string Name { get; set; } = string.Empty;

        [Id(1)]
        public string? Description { get; set; }

        /// <summary>
        /// Grants carried by this role. The <see cref="PermissionGrant.Subject"/> of these grants
        /// is implicitly the role itself and may be left empty.
        /// </summary>
        [Id(2)]
        public List<PermissionGrant> Grants { get; set; } = new();

        /// <summary>True for built-in roles (e.g. <c>acl-admin</c>). Protected from deletion.</summary>
        [Id(3)]
        public bool IsBuiltIn { get; set; }
    }

    /// <summary>
    /// A named set of members (principals and/or agents). Groups may also carry roles,
    /// inherited by every member. Membership is flat — groups do not nest.
    /// </summary>
    [GenerateSerializer]
    public sealed class AclGroup
    {
        /// <summary>Group name (case-insensitive unique id).</summary>
        [Id(0)]
        public string Name { get; set; } = string.Empty;

        [Id(1)]
        public string? Description { get; set; }

        /// <summary>Stored members. Empty for dynamic groups, whose membership is computed.</summary>
        [Id(2)]
        public List<GroupMember> Members { get; set; } = new();

        /// <summary>Roles inherited by all members of this group.</summary>
        [Id(3)]
        public List<string> Roles { get; set; } = new();

        /// <summary>
        /// True for the built-in dynamic groups (all principals / all agents), whose
        /// membership is computed rather than stored. Member edits are rejected.
        /// </summary>
        [Id(4)]
        public bool IsDynamic { get; set; }
    }

    /// <summary>A member of a group: a principal handle or a full "principal:agent" handle.</summary>
    [GenerateSerializer]
    public sealed class GroupMember
    {
        public GroupMember()
        {
        }

        public GroupMember(SubjectKind kind, string handle)
        {
            Kind = kind;
            Handle = handle;
        }

        /// <summary><see cref="SubjectKind.Principal"/> or <see cref="SubjectKind.Agent"/>.</summary>
        [Id(0)]
        public SubjectKind Kind { get; set; }

        /// <summary>Principal handle, or full "principal:agent" handle for agents.</summary>
        [Id(1)]
        public string Handle { get; set; } = string.Empty;
    }

    /// <summary>
    /// Default names for the built-in dynamic groups. The effective names are configurable
    /// via <c>FabrCoreAclOptions</c>; membership is always computed, never stored.
    /// </summary>
    public static class WellKnownGroups
    {
        /// <summary>Dynamic group containing every principal.</summary>
        public const string AllPrincipals = "all-principals";

        /// <summary>Dynamic group containing every agent.</summary>
        public const string AllAgents = "all-agents";

        /// <summary>Name of the built-in role granting <c>acl.manage.allow</c>.</summary>
        public const string AclAdminRole = "acl-admin";
    }
}
