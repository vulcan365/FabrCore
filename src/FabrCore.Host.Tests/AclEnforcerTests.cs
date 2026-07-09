using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class AclEnforcerTests
{
    private sealed class FixedSnapshotProvider : IAclSnapshotProvider
    {
        public FixedSnapshotProvider(AclSnapshotData data, FabrCoreAclOptions options)
            => Current = new AclSnapshot(data, options.AllPrincipalsGroupId, options.AllAgentsGroupId);

        public AclSnapshot Current { get; }
    }

    private static (AclEnforcer Enforcer, InMemoryAuditProvider Audit) CreateEnforcer(
        AclSnapshotData? data = null,
        Action<FabrCoreAclOptions>? configure = null,
        AuditOptions? auditOptions = null)
    {
        var options = new FabrCoreAclOptions();
        configure?.Invoke(options);

        var evaluator = new AclEvaluator(
            new FixedSnapshotProvider(data ?? new AclSnapshotData(), options),
            Options.Create(options),
            NullLogger<AclEvaluator>.Instance);

        var audit = new InMemoryAuditProvider(
            NullLogger<InMemoryAuditProvider>.Instance,
            Options.Create(auditOptions ?? new AuditOptions()));

        return (new AclEnforcer(evaluator, audit, NullLogger<AclEnforcer>.Instance), audit);
    }

    [TestMethod]
    public void Authorize_EnforceMode_ThrowsOnDenialAndAudits()
    {
        var (enforcer, audit) = CreateEnforcer();

        Assert.ThrowsExactly<AclDeniedException>(() =>
            enforcer.Authorize(new AclSubjectContext("p1", "p1:agent1"), FabrActions.AgentMessage, "p2:agent3"));

        var events = audit.GetEventsAsync().Result;
        Assert.HasCount(1, events);
        Assert.AreEqual(AuditCategory.AclDecision, events[0].Category);
        Assert.AreEqual(AuditOutcome.Denied, events[0].Outcome);
        Assert.IsTrue(events[0].WasEnforced);
        Assert.AreEqual("p1", events[0].SubjectPrincipal);
        Assert.AreEqual("p2", events[0].ResourcePrincipal);
    }

    [TestMethod]
    public void Authorize_DeniedException_IsUnauthorizedAccess()
    {
        var (enforcer, _) = CreateEnforcer();

        // Existing catch sites expect UnauthorizedAccessException — the denial must be catchable as one.
        try
        {
            enforcer.Authorize(new AclSubjectContext("p1", null), FabrActions.AgentMessage, "p2:agent3");
            Assert.Fail("Expected AclDeniedException.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.IsInstanceOfType<AclDeniedException>(ex);
        }
    }

    [TestMethod]
    public void Authorize_AuditOnlyMode_DoesNotThrowButAudits()
    {
        var (enforcer, audit) = CreateEnforcer(configure: o => o.Mode = AclEnforcementMode.AuditOnly);

        var decision = enforcer.Authorize(new AclSubjectContext("p1", null), FabrActions.AgentMessage, "p2:agent3");

        Assert.IsFalse(decision.IsAllowed);
        Assert.IsFalse(decision.ShouldBlock);

        var events = audit.GetEventsAsync().Result;
        Assert.HasCount(1, events);
        Assert.AreEqual(AuditOutcome.Denied, events[0].Outcome);
        Assert.IsFalse(events[0].WasEnforced);
        Assert.AreEqual(nameof(AclEnforcementMode.AuditOnly), events[0].EnforcementMode);
    }

    [TestMethod]
    public void Authorize_AllowedCrossPrincipal_Succeeds()
    {
        var data = new AclSnapshotData
        {
            Grants =
            {
                new PermissionGrant
                {
                    Subject = new AclSubject(SubjectKind.Principal, "p1"),
                    Permission = "agent.message.allow",
                    Resource = "p2:*"
                }
            }
        };
        var (enforcer, _) = CreateEnforcer(data);

        var decision = enforcer.Authorize(new AclSubjectContext("p1", "p1:agent1"), FabrActions.AgentMessage, "p2:agent3");

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.Allow, decision.Outcome);
    }

    [TestMethod]
    public void Authorize_AgentCreate_AuditsAsAgentCreationCategory()
    {
        var (enforcer, audit) = CreateEnforcer(configure: o => o.Mode = AclEnforcementMode.AuditOnly);

        enforcer.Authorize(new AclSubjectContext("p1", null), FabrActions.AgentCreate, "p2:*");

        var events = audit.GetEventsAsync().Result;
        Assert.HasCount(1, events);
        Assert.AreEqual(AuditCategory.AgentCreation, events[0].Category);
    }

    // ── Breadcrumb stamping and fan-out warning ──

    [TestMethod]
    public void StampAndWarn_FirstCrossing_SetsOriginAndHops()
    {
        var (enforcer, audit) = CreateEnforcer();
        var message = new AgentMessage { ToHandle = "p2:agent3" };

        enforcer.StampAndWarnCrossPrincipal(message, "p1", "p2");

        Assert.AreEqual("p1", message.CrossPrincipalOrigin);
        Assert.AreEqual(1, message.CrossPrincipalHops);

        var events = audit.GetEventsAsync().Result;
        Assert.HasCount(1, events);
        Assert.AreEqual(AuditCategory.BoundaryCrossing, events[0].Category);
    }

    [TestMethod]
    public void StampAndWarn_FanOut_KeepsOriginAndIncrementsHops()
    {
        var (enforcer, audit) = CreateEnforcer();
        var message = new AgentMessage { ToHandle = "p3:agent7" };

        // First crossing: p1 → p2; second crossing: p2 forwards to p3.
        enforcer.StampAndWarnCrossPrincipal(message, "p1", "p2");
        enforcer.StampAndWarnCrossPrincipal(message, "p2", "p3");

        Assert.AreEqual("p1", message.CrossPrincipalOrigin);
        Assert.AreEqual(2, message.CrossPrincipalHops);

        var events = audit.GetEventsAsync().Result;
        Assert.HasCount(2, events);
        // Most recent first: the fan-out event references the original principal.
        StringAssert.Contains(events[0].Reason, "p1");
        Assert.AreEqual("p2", events[0].SubjectPrincipal);
        Assert.AreEqual("p3", events[0].ResourcePrincipal);
    }

    [TestMethod]
    public void Response_CopiesBreadcrumb()
    {
        var message = new AgentMessage
        {
            FromHandle = "p1:agent1",
            ToHandle = "p2:agent3",
            CrossPrincipalOrigin = "p1",
            CrossPrincipalHops = 2
        };

        var response = message.Response();

        Assert.AreEqual("p1", response.CrossPrincipalOrigin);
        Assert.AreEqual(2, response.CrossPrincipalHops);
    }
}
