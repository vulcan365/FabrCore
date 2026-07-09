namespace FabrCore.Core.Acl
{
    /// <summary>
    /// The subject performing an operation. <see cref="AgentHandle"/> is the full
    /// <c>"principal:agent"</c> handle when the actor is an agent (agent-to-agent enforcement),
    /// or null when the actor is the principal itself (principal grain, controllers).
    /// </summary>
    public readonly record struct AclSubjectContext(string PrincipalHandle, string? AgentHandle);

    /// <summary>How an ACL decision was reached.</summary>
    public enum AclOutcome
    {
        /// <summary>A matching allow grant was found.</summary>
        Allow,

        /// <summary>A matching deny grant was found. Deny overrides allow.</summary>
        Deny,

        /// <summary>No grant matched — denied by default.</summary>
        NoMatchDeny,

        /// <summary>The subject and target belong to the same principal.</summary>
        ImplicitSamePrincipal,

        /// <summary>The subject is the unrestricted System principal.</summary>
        SystemBypass,

        /// <summary>Enforcement mode is <see cref="AclEnforcementMode.Disabled"/>.</summary>
        DisabledBypass
    }

    /// <summary>Result of an ACL evaluation.</summary>
    public sealed record AclDecision(
        AclOutcome Outcome,
        AclEnforcementMode Mode,
        string? Reason = null,
        PermissionGrant? DecidingGrant = null)
    {
        /// <summary>True when the operation is permitted.</summary>
        public bool IsAllowed => Outcome is AclOutcome.Allow
            or AclOutcome.ImplicitSamePrincipal
            or AclOutcome.SystemBypass
            or AclOutcome.DisabledBypass;

        /// <summary>True when a denial should actually block the operation (Enforce mode only).</summary>
        public bool ShouldBlock => !IsAllowed && Mode == AclEnforcementMode.Enforce;
    }

    /// <summary>
    /// Evaluates ACL decisions against the current snapshot.
    /// <para>
    /// <strong>Contract:</strong> <see cref="Evaluate"/> is synchronous, allocation-light, and
    /// backed by an immutable snapshot — it never performs I/O or grain calls and is safe to
    /// call inside grain turns on the per-message hot path.
    /// </para>
    /// <para>
    /// Resolution order: Disabled bypass → System-principal bypass → explicit deny →
    /// same-principal implicit allow → explicit allow → default deny. An explicit deny beats
    /// the same-principal implicit allow so that, e.g., <c>agent.create.deny</c> on <c>"*:*"</c>
    /// can disable agent creation entirely for a subject.
    /// </para>
    /// </summary>
    public interface IAclEvaluator
    {
        /// <summary>The effective enforcement mode.</summary>
        AclEnforcementMode Mode { get; }

        /// <summary>
        /// Evaluates whether <paramref name="subject"/> may perform <paramref name="action"/>
        /// on <paramref name="resourceHandle"/> (a full <c>"principal:agent"</c> handle or
        /// resource pattern target).
        /// </summary>
        AclDecision Evaluate(in AclSubjectContext subject, AclAction action, string resourceHandle);
    }

    /// <summary>Typed convenience wrappers over <see cref="IAclEvaluator.Evaluate"/>.</summary>
    public static class AclEvaluatorExtensions
    {
        /// <summary>May the subject send a message/event to the target agent?</summary>
        public static AclDecision CanSendMessage(
            this IAclEvaluator evaluator, string fromPrincipal, string? fromAgentFullHandle, string toFullHandle)
            => evaluator.Evaluate(new AclSubjectContext(fromPrincipal, fromAgentFullHandle), FabrActions.AgentMessage, toFullHandle);

        /// <summary>May the creator principal create agents under the target principal?</summary>
        public static AclDecision CanCreateAgent(
            this IAclEvaluator evaluator, string creatorPrincipal, string targetPrincipal)
            => evaluator.Evaluate(new AclSubjectContext(creatorPrincipal, null), FabrActions.AgentCreate, targetPrincipal + ":*");

        /// <summary>May the reader principal read the target agent's threads/state/monitor data?</summary>
        public static AclDecision CanRead(
            this IAclEvaluator evaluator, string readerPrincipal, string targetFullHandle)
            => evaluator.Evaluate(new AclSubjectContext(readerPrincipal, null), FabrActions.AgentRead, targetFullHandle);

        /// <summary>May the principal manage ACL entities?</summary>
        public static AclDecision CanManageAcl(this IAclEvaluator evaluator, string principal)
            => evaluator.Evaluate(new AclSubjectContext(principal, null), FabrActions.AclManage, "*:*");

        /// <summary>May the principal read ACL entities and run evaluate/check queries?</summary>
        public static AclDecision CanReadAcl(this IAclEvaluator evaluator, string principal)
            => evaluator.Evaluate(new AclSubjectContext(principal, null), FabrActions.AclRead, "*:*");
    }
}
