using Orleans;

namespace FabrCore.Core.Acl
{
    /// <summary>
    /// The subject a <see cref="PermissionGrant"/> applies to. Selectors are exact
    /// (case-insensitive) — there are no wildcards on the subject side; "any principal"
    /// is expressed by targeting the dynamic all-principals group.
    /// </summary>
    [GenerateSerializer]
    public sealed class AclSubject
    {
        public AclSubject()
        {
        }

        public AclSubject(SubjectKind kind, string selector)
        {
            Kind = kind;
            Selector = selector;
        }

        [Id(0)]
        public SubjectKind Kind { get; set; }

        /// <summary>
        /// Principal handle, full "principal:agent" handle, role name, or group name,
        /// depending on <see cref="Kind"/>.
        /// </summary>
        [Id(1)]
        public string Selector { get; set; } = string.Empty;

        /// <summary>
        /// Parses the compact string form used in configuration seeds and the API:
        /// <c>"principal:alice"</c>, <c>"agent:alice:agent1"</c>, <c>"role:ops"</c>, <c>"group:tenants"</c>.
        /// </summary>
        public static AclSubject Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new FormatException("Subject cannot be empty.");

            var separator = value.IndexOf(':');
            if (separator <= 0 || separator == value.Length - 1)
                throw new FormatException(
                    $"Invalid subject '{value}'. Expected 'principal:<handle>', 'agent:<principal>:<agent>', 'role:<name>', or 'group:<name>'.");

            var kindToken = value[..separator];
            var selector = value[(separator + 1)..];

            var kind = kindToken.ToLowerInvariant() switch
            {
                "principal" => SubjectKind.Principal,
                "agent" => SubjectKind.Agent,
                "role" => SubjectKind.Role,
                "group" => SubjectKind.Group,
                _ => throw new FormatException(
                    $"Invalid subject kind '{kindToken}' in '{value}'. Expected principal, agent, role, or group.")
            };

            if (kind == SubjectKind.Agent && !selector.Contains(':'))
                throw new FormatException(
                    $"Invalid agent subject '{value}'. Agent selectors must be full 'principal:agent' handles.");

            return new AclSubject(kind, selector);
        }

        public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Selector}";
    }

    /// <summary>
    /// Grants (or explicitly denies) a permission to a subject over a resource scope.
    /// <para>
    /// <see cref="Resource"/> is a pattern matched against a full <c>"principal:agent"</c> handle:
    /// <list type="bullet">
    ///   <item><c>"p2:agent3"</c> — exact agent</item>
    ///   <item><c>"p2:*"</c> — all agents of principal p2</item>
    ///   <item><c>"*:agent5"</c> — the agent named agent5 under any principal</item>
    ///   <item><c>"*:*"</c> — everything</item>
    ///   <item><c>"prefix*"</c> — starts-with, per segment</item>
    ///   <item><c>"group:tenants:*"</c> — agents of any principal in group 'tenants' (group refs are valid on the principal segment only)</item>
    /// </list>
    /// For <c>agent.create</c> the agent segment is conventionally <c>"*"</c> — the meaningful
    /// part is the principal segment ("for whom may agents be created").
    /// Application-defined permissions that need no resource scope use <c>"*:*"</c>.
    /// </para>
    /// </summary>
    [GenerateSerializer]
    public sealed class PermissionGrant
    {
        /// <summary>Unique id. Generated when omitted.</summary>
        [Id(0)]
        public string Id { get; set; } = Guid.NewGuid().ToString("n");

        /// <summary>
        /// Who the grant applies to. May be null on grants embedded in an <see cref="AclRole"/>,
        /// where the subject is implicitly the role.
        /// </summary>
        [Id(1)]
        public AclSubject? Subject { get; set; }

        /// <summary>Permission name in 3-dot notation, e.g. <c>"agent.message.allow"</c>.</summary>
        [Id(2)]
        public string Permission { get; set; } = string.Empty;

        /// <summary>Resource pattern (see class remarks). Defaults to <c>"*:*"</c>.</summary>
        [Id(3)]
        public string Resource { get; set; } = "*:*";

        /// <summary>Parses <see cref="Permission"/> into its validated form.</summary>
        public PermissionName GetPermissionName() => PermissionName.Parse(Permission);
    }
}
