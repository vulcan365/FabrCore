namespace FabrCore.Core.Acl
{
    /// <summary>How ACL decisions are applied at enforcement points.</summary>
    public enum AclEnforcementMode
    {
        /// <summary>No evaluation — everything is allowed. Not recommended outside isolated dev.</summary>
        Disabled,

        /// <summary>Evaluate and audit would-be denials, but allow the operation to proceed.</summary>
        AuditOnly,

        /// <summary>Evaluate, audit, and block denied operations. The default.</summary>
        Enforce
    }

    /// <summary>
    /// ACL configuration. Binds from the <c>"Acl"</c> section (or <c>"FabrCore:Acl"</c> for hosts
    /// that wrap settings under a FabrCore node) of fabrcore.json. All defaults are safe for
    /// zero-config use: principals have full access to their own agents, cross-principal traffic
    /// is denied until granted, and the System principal is unrestricted.
    /// </summary>
    public class FabrCoreAclOptions
    {
        public const string SectionName = "FabrCore:Acl";

        /// <summary>
        /// Handle of the built-in unrestricted System principal. Treated like a credential
        /// name — callers presenting this handle bypass all ACL checks.
        /// </summary>
        public string SystemPrincipal { get; set; } = "system";

        /// <summary>Enforcement mode. Default <see cref="AclEnforcementMode.Enforce"/>.</summary>
        public AclEnforcementMode Mode { get; set; } = AclEnforcementMode.Enforce;

        /// <summary>Name of the dynamic group containing every principal.</summary>
        public string AllPrincipalsGroupId { get; set; } = WellKnownGroups.AllPrincipals;

        /// <summary>Name of the dynamic group containing every agent.</summary>
        public string AllAgentsGroupId { get; set; } = WellKnownGroups.AllAgents;

        /// <summary>
        /// Maximum staleness of a silo's cached ACL snapshot when change notifications are
        /// missed. Default 30 seconds.
        /// </summary>
        public int CacheTtlSeconds { get; set; } = 30;

        /// <summary>
        /// When the grant store is empty at bootstrap, seed all-principals → message + read
        /// access to the System principal's agents. Preserves the zero-config
        /// shared-system-agent demo experience. Default true.
        /// </summary>
        public bool SeedDefaultSystemAgentAccess { get; set; } = true;

        /// <summary>Optional entities applied once at first bootstrap (dev/demo convenience).</summary>
        public AclSeedOptions? Seed { get; set; }
    }

    /// <summary>
    /// Config-declared ACL entities applied at first bootstrap only. Plain string-property
    /// DTOs so <c>IConfiguration</c> binding works.
    /// </summary>
    public class AclSeedOptions
    {
        public List<AclPrincipalSeed> Principals { get; set; } = new();
        public List<AclRoleSeed> Roles { get; set; } = new();
        public List<AclGroupSeed> Groups { get; set; } = new();
        public List<AclGrantSeed> Grants { get; set; } = new();
    }

    public class AclPrincipalSeed
    {
        public string Handle { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public List<string> Roles { get; set; } = new();
    }

    public class AclRoleSeed
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Grants carried by the role; the role itself is the implicit subject.</summary>
        public List<AclGrantSeed> Grants { get; set; } = new();
    }

    public class AclGroupSeed
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Members in compact form: <c>"principal:alice"</c> or <c>"agent:alice:agent1"</c>.</summary>
        public List<string> Members { get; set; } = new();

        public List<string> Roles { get; set; } = new();
    }

    public class AclGrantSeed
    {
        /// <summary>
        /// Compact subject: <c>"principal:alice"</c>, <c>"agent:alice:agent1"</c>,
        /// <c>"role:ops"</c>, or <c>"group:tenants"</c>. Empty for grants nested in a role seed.
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>3-dot permission name, e.g. <c>"agent.message.allow"</c>.</summary>
        public string Permission { get; set; } = string.Empty;

        /// <summary>Resource pattern. Defaults to <c>"*:*"</c>.</summary>
        public string Resource { get; set; } = "*:*";
    }
}
