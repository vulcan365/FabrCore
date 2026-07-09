using System.Diagnostics;

using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Shared enforcement helper wrapping <see cref="IAclEvaluator"/> and <see cref="IAuditProvider"/>
    /// so grains and controllers don't duplicate mode handling, audit emission, or the
    /// cross-principal breadcrumb logic. Audit recording is fire-and-forget safe — it never
    /// throws into the enforcement path.
    /// </summary>
    public sealed class AclEnforcer
    {
        private readonly IAclEvaluator _evaluator;
        private readonly IAuditProvider _audit;
        private readonly ILogger<AclEnforcer> _logger;

        public AclEnforcer(IAclEvaluator evaluator, IAuditProvider audit, ILogger<AclEnforcer> logger)
        {
            _evaluator = evaluator;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>The underlying evaluator, for read-only checks that never throw.</summary>
        public IAclEvaluator Evaluator => _evaluator;

        /// <summary>
        /// Evaluates and audits an ACL decision. In <see cref="AclEnforcementMode.Enforce"/> mode
        /// a denial throws <see cref="AclDeniedException"/>; in <see cref="AclEnforcementMode.AuditOnly"/>
        /// a would-be denial logs a warning and proceeds.
        /// </summary>
        public AclDecision Authorize(in AclSubjectContext subject, AclAction action, string resource, AgentMessage? message = null)
        {
            var decision = _evaluator.Evaluate(subject, action, resource);

            // Disabled mode skips evaluation entirely — nothing meaningful to audit per call.
            if (decision.Outcome == AclOutcome.DisabledBypass)
                return decision;

            RecordDecision(subject, action, resource, decision, message);

            if (!decision.IsAllowed)
            {
                if (decision.ShouldBlock)
                {
                    _logger.LogWarning("ACL denied: '{Subject}' cannot {Action} on '{Resource}'. {Reason}",
                        subject.AgentHandle ?? subject.PrincipalHandle, action, resource, decision.Reason);
                    throw new AclDeniedException(
                        $"Access denied: '{subject.AgentHandle ?? subject.PrincipalHandle}' cannot {action} on '{resource}'. {decision.Reason}");
                }

                _logger.LogWarning("ACL AuditOnly: would deny '{Subject}' {Action} on '{Resource}'. {Reason}",
                    subject.AgentHandle ?? subject.PrincipalHandle, action, resource, decision.Reason);
            }

            return decision;
        }

        /// <summary>
        /// Stamps the cross-principal breadcrumb on a message crossing a principal boundary and
        /// warns/audits when a chain that originated at another principal fans out across a
        /// second boundary. Never blocks.
        /// </summary>
        public void StampAndWarnCrossPrincipal(AgentMessage message, string sendingPrincipal, string targetPrincipal)
        {
            message.CrossPrincipalHops++;

            if (message.CrossPrincipalOrigin is null)
            {
                message.CrossPrincipalOrigin = sendingPrincipal;
                RecordBoundaryCrossing(message, sendingPrincipal, targetPrincipal, fanOut: false);
                return;
            }

            if (!string.Equals(message.CrossPrincipalOrigin, sendingPrincipal, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Cross-principal fan-out: message {MessageId} originating from principal '{Origin}' is being " +
                    "forwarded by '{Sender}' to '{Target}' (boundary crossing {Hops})",
                    message.Id, message.CrossPrincipalOrigin, sendingPrincipal, targetPrincipal, message.CrossPrincipalHops);
                RecordBoundaryCrossing(message, sendingPrincipal, targetPrincipal, fanOut: true);
            }
        }

        /// <summary>
        /// Logs a same-principal hop of a message chain that previously crossed a principal
        /// boundary. Advisory only — internal fan-out after an authorized crossing is expected.
        /// </summary>
        public void NoteTaggedSamePrincipalHop(AgentMessage message, string principal, string targetHandle)
        {
            _logger.LogDebug(
                "Message {MessageId} (cross-principal origin '{Origin}', {Hops} crossings) hopping within principal '{Principal}' to '{Target}'",
                message.Id, message.CrossPrincipalOrigin, message.CrossPrincipalHops, principal, targetHandle);
        }

        private void RecordDecision(in AclSubjectContext subject, AclAction action, string resource, AclDecision decision, AgentMessage? message)
        {
            var (resourcePrincipal, _) = HandleUtilities.ParseHandle(resource);

            var auditEvent = new AuditEvent
            {
                Category = action == FabrActions.AgentCreate ? AuditCategory.AgentCreation : AuditCategory.AclDecision,
                Outcome = decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Denied,
                SubjectPrincipal = subject.PrincipalHandle,
                SubjectAgent = subject.AgentHandle,
                ResourcePrincipal = resourcePrincipal,
                Resource = resource,
                Permission = action.ToString(),
                EnforcementMode = decision.Mode.ToString(),
                WasEnforced = decision.ShouldBlock,
                Reason = decision.Reason,
                TraceId = Activity.Current?.TraceId.ToString(),
                VerifiableExecutionId = message?.VerifiableExecution?.TraceId
            };

            if (message is not null)
                auditEvent.Details["messageId"] = message.Id;

            Record(auditEvent);
        }

        private void RecordBoundaryCrossing(AgentMessage message, string sendingPrincipal, string targetPrincipal, bool fanOut)
        {
            var auditEvent = new AuditEvent
            {
                Category = AuditCategory.BoundaryCrossing,
                Outcome = AuditOutcome.Success,
                SubjectPrincipal = sendingPrincipal,
                ResourcePrincipal = targetPrincipal,
                Resource = message.ToHandle,
                EnforcementMode = _evaluator.Mode.ToString(),
                WasEnforced = false,
                Reason = fanOut
                    ? $"Fan-out: chain originating from '{message.CrossPrincipalOrigin}' crossed another principal boundary"
                    : "First cross-principal boundary crossing",
                TraceId = Activity.Current?.TraceId.ToString(),
                VerifiableExecutionId = message.VerifiableExecution?.TraceId
            };

            auditEvent.Details["messageId"] = message.Id;
            auditEvent.Details["origin"] = message.CrossPrincipalOrigin ?? sendingPrincipal;
            auditEvent.Details["hops"] = message.CrossPrincipalHops.ToString();

            Record(auditEvent);
        }

        private void Record(AuditEvent auditEvent)
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
    }
}
