namespace FabrCore.Core.Acl
{
    /// <summary>Request body for the ACL evaluate endpoint (dry-run decision with full detail).</summary>
    public class AclEvaluationRequest
    {
        /// <summary>Principal handle of the subject being evaluated.</summary>
        public string SubjectPrincipal { get; set; } = string.Empty;

        /// <summary>Optional full "principal:agent" handle when the subject acts as an agent.</summary>
        public string? SubjectAgent { get; set; }

        /// <summary>Action stem in <c>entity.behavior</c> form, e.g. <c>"agent.message"</c>.</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Target resource handle or pattern target. Defaults to <c>"*:*"</c>.</summary>
        public string Resource { get; set; } = "*:*";
    }

    /// <summary>Result of an ACL evaluate/check call.</summary>
    public class AclEvaluationResponse
    {
        public bool Allowed { get; set; }

        /// <summary>The <see cref="AclOutcome"/> name.</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>The effective <see cref="AclEnforcementMode"/> name.</summary>
        public string Mode { get; set; } = string.Empty;

        public string? Reason { get; set; }

        /// <summary>The grant that decided the outcome, when one matched.</summary>
        public PermissionGrant? DecidingGrant { get; set; }
    }

    /// <summary>
    /// Request body for the simplified check endpoint used by applications gating their own
    /// features (e.g. "does this principal have surface.adminview?").
    /// </summary>
    public class AclCheckRequest
    {
        /// <summary>Principal handle to check.</summary>
        public string Principal { get; set; } = string.Empty;

        /// <summary>Action stem in <c>entity.behavior</c> form, e.g. <c>"surface.adminview"</c>.</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Optional resource; defaults to <c>"*:*"</c> for resource-less app permissions.</summary>
        public string? Resource { get; set; }
    }

    /// <summary>ACL configuration and status, as returned by the config endpoint.</summary>
    public class AclConfigResponse
    {
        public string SystemPrincipal { get; set; } = string.Empty;

        /// <summary>Mode from configuration (fabrcore.json).</summary>
        public string ConfiguredMode { get; set; } = string.Empty;

        /// <summary>Runtime override set via the API, if any.</summary>
        public string? ModeOverride { get; set; }

        /// <summary>The mode actually in effect (override wins over configuration).</summary>
        public string EffectiveMode { get; set; } = string.Empty;

        public string AllPrincipalsGroupId { get; set; } = string.Empty;
        public string AllAgentsGroupId { get; set; } = string.Empty;

        /// <summary>Current ACL entity-set version.</summary>
        public long SnapshotVersion { get; set; }
    }

    /// <summary>Request body for setting (or clearing) the runtime enforcement-mode override.</summary>
    public class SetEnforcementModeRequest
    {
        /// <summary><c>"Disabled"</c>, <c>"AuditOnly"</c>, <c>"Enforce"</c>, or null to clear the override.</summary>
        public string? Mode { get; set; }
    }
}
