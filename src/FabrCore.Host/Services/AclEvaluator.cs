using FabrCore.Core;
using FabrCore.Core.Acl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default synchronous <see cref="IAclEvaluator"/>. Evaluates against the immutable
    /// snapshot from <see cref="IAclSnapshotProvider"/> — no locks, no I/O, no grain calls.
    /// <para>
    /// Resolution order: Disabled bypass → System-principal bypass → explicit deny →
    /// same-principal implicit allow → explicit allow → default deny.
    /// </para>
    /// </summary>
    public class AclEvaluator : IAclEvaluator
    {
        private readonly IAclSnapshotProvider _snapshotProvider;
        private readonly FabrCoreAclOptions _options;
        private readonly ILogger<AclEvaluator> _logger;

        public AclEvaluator(
            IAclSnapshotProvider snapshotProvider,
            IOptions<FabrCoreAclOptions> options,
            ILogger<AclEvaluator> logger)
        {
            _snapshotProvider = snapshotProvider;
            _options = options.Value;
            _logger = logger;
        }

        /// <inheritdoc />
        public AclEnforcementMode Mode => _snapshotProvider.Current.ModeOverride ?? _options.Mode;

        /// <inheritdoc />
        public AclDecision Evaluate(in AclSubjectContext subject, AclAction action, string resourceHandle)
        {
            // One snapshot read for the whole evaluation — a consistent view.
            var snapshot = _snapshotProvider.Current;
            var mode = snapshot.ModeOverride ?? _options.Mode;

            if (mode == AclEnforcementMode.Disabled)
                return new AclDecision(AclOutcome.DisabledBypass, mode, "ACL enforcement is disabled");

            if (string.Equals(subject.PrincipalHandle, _options.SystemPrincipal, StringComparison.OrdinalIgnoreCase))
                return new AclDecision(AclOutcome.SystemBypass, mode, "System principal is unrestricted");

            var (targetPrincipal, targetAgent) = HandleUtilities.ParseHandle(resourceHandle);

            PermissionGrant? denyGrant = null;
            PermissionGrant? allowGrant = null;

            foreach (var grant in snapshot.GrantsFor(action))
            {
                if (!SubjectMatches(snapshot, grant.Subject, subject))
                    continue;

                if (!HandleScopeMatcher.Matches(grant.Resource, targetPrincipal, targetAgent, snapshot.IsPrincipalInGroup))
                    continue;

                if (!PermissionName.TryParse(grant.Permission, out var permission))
                    continue;

                if (permission.Effect == PermissionEffect.Deny)
                {
                    denyGrant = grant;
                    break; // deny overrides allow — no need to keep scanning
                }

                allowGrant ??= grant;
            }

            if (denyGrant is not null)
            {
                return new AclDecision(
                    AclOutcome.Deny, mode,
                    $"Denied by grant '{denyGrant.Permission}' for subject '{denyGrant.Subject}' on '{denyGrant.Resource}'",
                    denyGrant);
            }

            // Same-principal traffic is implicitly allowed — after explicit deny so that
            // e.g. agent.create.deny can disable an action entirely for a subject.
            if (string.Equals(subject.PrincipalHandle, targetPrincipal, StringComparison.OrdinalIgnoreCase))
                return new AclDecision(AclOutcome.ImplicitSamePrincipal, mode, "Subject and target share a principal");

            if (allowGrant is not null)
            {
                return new AclDecision(
                    AclOutcome.Allow, mode,
                    $"Allowed by grant '{allowGrant.Permission}' for subject '{allowGrant.Subject}' on '{allowGrant.Resource}'",
                    allowGrant);
            }

            return new AclDecision(
                AclOutcome.NoMatchDeny, mode,
                $"No grant permits '{FormatSubject(subject)}' to {action} on '{resourceHandle}'");
        }

        private static bool SubjectMatches(AclSnapshot snapshot, AclSubject? grantSubject, in AclSubjectContext subject)
        {
            if (grantSubject is null || string.IsNullOrEmpty(grantSubject.Selector))
                return false;

            switch (grantSubject.Kind)
            {
                case SubjectKind.Principal:
                    return string.Equals(grantSubject.Selector, subject.PrincipalHandle, StringComparison.OrdinalIgnoreCase);

                case SubjectKind.Agent:
                    return subject.AgentHandle is not null &&
                        string.Equals(grantSubject.Selector, subject.AgentHandle, StringComparison.OrdinalIgnoreCase);

                case SubjectKind.Role:
                    foreach (var role in snapshot.RolesOf(subject.PrincipalHandle))
                    {
                        if (string.Equals(role, grantSubject.Selector, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;

                case SubjectKind.Group:
                    if (snapshot.IsPrincipalInGroup(grantSubject.Selector, subject.PrincipalHandle))
                        return true;
                    return subject.AgentHandle is not null &&
                        snapshot.IsAgentInGroup(grantSubject.Selector, subject.AgentHandle);

                default:
                    return false;
            }
        }

        private static string FormatSubject(in AclSubjectContext subject)
            => subject.AgentHandle is null ? subject.PrincipalHandle : subject.AgentHandle;
    }
}
