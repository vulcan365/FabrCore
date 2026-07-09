using Orleans;

namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Supplies the current ACL snapshot to the evaluator.
    /// <para>
    /// <strong>Contract:</strong> <see cref="Current"/> must be non-blocking — a volatile read
    /// of an immutable snapshot reference. It must NEVER make a grain call or perform I/O:
    /// it is read inside grain turns on the per-message hot path. Implementations refresh the
    /// snapshot out-of-band (change-stream push, background timer) and swap the reference.
    /// </para>
    /// </summary>
    public interface IAclSnapshotProvider
    {
        /// <summary>The current immutable ACL snapshot.</summary>
        AclSnapshot Current { get; }
    }

    /// <summary>
    /// Serializable ACL entity set, as transferred from the registry grain or built from
    /// configuration seeds. Build an indexed <see cref="AclSnapshot"/> from it for evaluation.
    /// </summary>
    [GenerateSerializer]
    public sealed class AclSnapshotData
    {
        /// <summary>Monotonic version, bumped on every mutation.</summary>
        [Id(0)]
        public long Version { get; set; }

        [Id(1)]
        public List<AclPrincipal> Principals { get; set; } = new();

        [Id(2)]
        public List<AclRole> Roles { get; set; } = new();

        [Id(3)]
        public List<AclGroup> Groups { get; set; } = new();

        [Id(4)]
        public List<PermissionGrant> Grants { get; set; } = new();

        /// <summary>Runtime enforcement-mode override set via the management API, if any.</summary>
        [Id(5)]
        public AclEnforcementMode? ModeOverride { get; set; }
    }

    /// <summary>
    /// An immutable, indexed view of all ACL entities, built once per version and shared
    /// lock-free across evaluations. Role grants are flattened into the per-action index with
    /// the role as subject; reverse group-membership and effective-role maps are precomputed.
    /// </summary>
    public sealed class AclSnapshot
    {
        private static readonly IReadOnlyList<PermissionGrant> EmptyGrants = Array.Empty<PermissionGrant>();
        private static readonly IReadOnlyList<string> EmptyNames = Array.Empty<string>();

        private readonly Dictionary<string, IReadOnlyList<PermissionGrant>> _grantsByAction;
        private readonly Dictionary<string, IReadOnlyList<string>> _groupsOfPrincipal;
        private readonly Dictionary<string, IReadOnlyList<string>> _groupsOfAgent;
        private readonly Dictionary<string, IReadOnlyList<string>> _rolesOfPrincipal;
        private readonly IReadOnlyList<string> _rolesForEveryPrincipal;
        private readonly string _allPrincipalsGroup;
        private readonly string _allAgentsGroup;

        public long Version { get; }

        /// <summary>Runtime enforcement-mode override set via the management API, if any.</summary>
        public AclEnforcementMode? ModeOverride { get; }

        public IReadOnlyDictionary<string, AclPrincipal> Principals { get; }
        public IReadOnlyDictionary<string, AclRole> Roles { get; }
        public IReadOnlyDictionary<string, AclGroup> Groups { get; }
        public IReadOnlyList<PermissionGrant> Grants { get; }

        public AclSnapshot(AclSnapshotData data, string allPrincipalsGroup, string allAgentsGroup)
        {
            Version = data.Version;
            ModeOverride = data.ModeOverride;
            _allPrincipalsGroup = allPrincipalsGroup;
            _allAgentsGroup = allAgentsGroup;

            var principals = new Dictionary<string, AclPrincipal>(StringComparer.OrdinalIgnoreCase);
            foreach (var principal in data.Principals)
                principals[principal.Handle] = principal;
            Principals = principals;

            var roles = new Dictionary<string, AclRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in data.Roles)
                roles[role.Name] = role;
            Roles = roles;

            var groups = new Dictionary<string, AclGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in data.Groups)
                groups[group.Name] = group;
            Groups = groups;

            Grants = data.Grants;

            // Reverse group-membership indexes (stored membership only; dynamic groups are
            // resolved at query time).
            var groupsOfPrincipal = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var groupsOfAgent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in data.Groups)
            {
                foreach (var member in group.Members)
                {
                    var index = member.Kind == SubjectKind.Agent ? groupsOfAgent : groupsOfPrincipal;
                    if (!index.TryGetValue(member.Handle, out var list))
                        index[member.Handle] = list = new List<string>();
                    list.Add(group.Name);
                }
            }
            _groupsOfPrincipal = ToReadOnly(groupsOfPrincipal);
            _groupsOfAgent = ToReadOnly(groupsOfAgent);

            // Roles carried by dynamic groups apply to every principal.
            var rolesForEveryone = new List<string>();
            foreach (var group in data.Groups)
            {
                if (group.IsDynamic && group.Roles.Count > 0)
                    rolesForEveryone.AddRange(group.Roles);
            }
            _rolesForEveryPrincipal = rolesForEveryone.Count > 0 ? rolesForEveryone : EmptyNames;

            // Effective roles per principal: direct + via stored group membership + dynamic-group roles.
            var rolesOfPrincipal = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var principal in data.Principals)
            {
                var effective = new List<string>(principal.Roles);
                if (_groupsOfPrincipal.TryGetValue(principal.Handle, out var memberOf))
                {
                    foreach (var groupName in memberOf)
                    {
                        if (groups.TryGetValue(groupName, out var group))
                            effective.AddRange(group.Roles);
                    }
                }
                effective.AddRange(_rolesForEveryPrincipal);
                rolesOfPrincipal[principal.Handle] = effective;
            }
            _rolesOfPrincipal = rolesOfPrincipal;

            // Per-action grant index. Role grants are flattened in with the role as subject.
            var grantsByAction = new Dictionary<string, List<PermissionGrant>>(StringComparer.Ordinal);
            void Index(PermissionGrant grant)
            {
                if (!PermissionName.TryParse(grant.Permission, out var permission))
                    return; // invalid names are rejected at write time; skip defensively here
                var key = permission.Action.ToString();
                if (!grantsByAction.TryGetValue(key, out var list))
                    grantsByAction[key] = list = new List<PermissionGrant>();
                list.Add(grant);
            }

            foreach (var grant in data.Grants)
                Index(grant);

            foreach (var role in data.Roles)
            {
                foreach (var grant in role.Grants)
                {
                    Index(new PermissionGrant
                    {
                        Id = grant.Id,
                        Subject = new AclSubject(SubjectKind.Role, role.Name),
                        Permission = grant.Permission,
                        Resource = grant.Resource
                    });
                }
            }

            _grantsByAction = ToReadOnly(grantsByAction);
        }

        /// <summary>Grants (role grants flattened in) whose permission stem matches the action.</summary>
        public IReadOnlyList<PermissionGrant> GrantsFor(AclAction action)
            => _grantsByAction.TryGetValue(action.ToString(), out var grants) ? grants : EmptyGrants;

        /// <summary>Stored group memberships of a principal or agent (dynamic groups excluded).</summary>
        public IReadOnlyList<string> GroupsOf(SubjectKind kind, string handle)
        {
            var index = kind == SubjectKind.Agent ? _groupsOfAgent : _groupsOfPrincipal;
            return index.TryGetValue(handle, out var groups) ? groups : EmptyNames;
        }

        /// <summary>Effective roles of a principal: direct, via groups, and via dynamic groups.</summary>
        public IReadOnlyList<string> RolesOf(string principalHandle)
            => _rolesOfPrincipal.TryGetValue(principalHandle, out var roles) ? roles : _rolesForEveryPrincipal;

        /// <summary>
        /// Whether a principal is a member of the named group, including the dynamic
        /// all-principals group (every principal is a member).
        /// </summary>
        public bool IsPrincipalInGroup(string groupName, string principalHandle)
        {
            if (string.Equals(groupName, _allPrincipalsGroup, StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var name in GroupsOf(SubjectKind.Principal, principalHandle))
            {
                if (string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an agent (full "principal:agent" handle) is a member of the named group,
        /// including the dynamic all-agents group (every agent is a member).
        /// </summary>
        public bool IsAgentInGroup(string groupName, string agentFullHandle)
        {
            if (string.Equals(groupName, _allAgentsGroup, StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var name in GroupsOf(SubjectKind.Agent, agentFullHandle))
            {
                if (string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Name of the dynamic group containing every principal.</summary>
        public string AllPrincipalsGroup => _allPrincipalsGroup;

        /// <summary>Name of the dynamic group containing every agent.</summary>
        public string AllAgentsGroup => _allAgentsGroup;

        private static Dictionary<string, IReadOnlyList<T>> ToReadOnly<T>(Dictionary<string, List<T>> source)
        {
            var result = new Dictionary<string, IReadOnlyList<T>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in source)
                result[key] = value;
            return result;
        }

        private static Dictionary<string, IReadOnlyList<PermissionGrant>> ToReadOnly(
            Dictionary<string, List<PermissionGrant>> source)
        {
            var result = new Dictionary<string, IReadOnlyList<PermissionGrant>>(StringComparer.Ordinal);
            foreach (var (key, value) in source)
                result[key] = value;
            return result;
        }
    }
}
