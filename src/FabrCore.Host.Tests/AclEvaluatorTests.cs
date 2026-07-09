using FabrCore.Core.Acl;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class AclEvaluatorTests
{
    private sealed class FixedSnapshotProvider : IAclSnapshotProvider
    {
        public FixedSnapshotProvider(AclSnapshotData data, FabrCoreAclOptions options)
            => Current = new AclSnapshot(data, options.AllPrincipalsGroupId, options.AllAgentsGroupId);

        public AclSnapshot Current { get; }
    }

    private static AclEvaluator CreateEvaluator(AclSnapshotData data, Action<FabrCoreAclOptions>? configure = null)
    {
        var options = new FabrCoreAclOptions();
        configure?.Invoke(options);
        return new AclEvaluator(
            new FixedSnapshotProvider(data, options),
            Options.Create(options),
            NullLogger<AclEvaluator>.Instance);
    }

    private static PermissionGrant Grant(AclSubject subject, string permission, string resource)
        => new() { Subject = subject, Permission = permission, Resource = resource };

    // ── Defaults and bypasses ──

    [TestMethod]
    public void SamePrincipal_IsImplicitlyAllowed()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData());

        var decision = evaluator.CanSendMessage("p1", "p1:agent1", "p1:agent2");

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.ImplicitSamePrincipal, decision.Outcome);
    }

    [TestMethod]
    public void CrossPrincipal_NoGrant_IsDeniedByDefault()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData());

        var decision = evaluator.CanSendMessage("p1", "p1:agent1", "p2:agent3");

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.NoMatchDeny, decision.Outcome);
        Assert.IsTrue(decision.ShouldBlock);
    }

    [TestMethod]
    public void SystemPrincipal_BypassesAllChecks()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData());

        var decision = evaluator.CanSendMessage("system", null, "p2:agent3");

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.SystemBypass, decision.Outcome);
    }

    [TestMethod]
    public void ConfiguredSystemPrincipal_Bypasses()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData(), o => o.SystemPrincipal = "root");

        Assert.AreEqual(AclOutcome.SystemBypass, evaluator.CanSendMessage("root", null, "p2:x").Outcome);
        Assert.AreEqual(AclOutcome.NoMatchDeny, evaluator.CanSendMessage("system", null, "p2:x").Outcome);
    }

    [TestMethod]
    public void DisabledMode_BypassesEverything()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData(), o => o.Mode = AclEnforcementMode.Disabled);

        var decision = evaluator.CanSendMessage("p1", null, "p2:agent3");

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.DisabledBypass, decision.Outcome);
    }

    [TestMethod]
    public void AuditOnlyMode_DeniesButDoesNotBlock()
    {
        var evaluator = CreateEvaluator(new AclSnapshotData(), o => o.Mode = AclEnforcementMode.AuditOnly);

        var decision = evaluator.CanSendMessage("p1", null, "p2:agent3");

        Assert.IsFalse(decision.IsAllowed);
        Assert.IsFalse(decision.ShouldBlock);
    }

    [TestMethod]
    public void SnapshotModeOverride_WinsOverConfiguration()
    {
        var data = new AclSnapshotData { ModeOverride = AclEnforcementMode.Disabled };
        var evaluator = CreateEvaluator(data, o => o.Mode = AclEnforcementMode.Enforce);

        Assert.AreEqual(AclEnforcementMode.Disabled, evaluator.Mode);
        Assert.AreEqual(AclOutcome.DisabledBypass, evaluator.CanSendMessage("p1", null, "p2:x").Outcome);
    }

    // ── The three cross-talk grant scopes ──

    [TestMethod]
    public void ScopeA_SpecificAgentToSpecificAgent()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Agent, "p1:agent1"), "agent.message.allow", "p2:agent3") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", "p1:agent1", "p2:agent3").IsAllowed);
        // Other agents of p1 are not covered by the agent-scoped grant.
        Assert.IsFalse(evaluator.CanSendMessage("p1", "p1:agent2", "p2:agent3").IsAllowed);
        // The principal itself (no acting agent) is not covered either.
        Assert.IsFalse(evaluator.CanSendMessage("p1", null, "p2:agent3").IsAllowed);
        // Other target agents are not covered.
        Assert.IsFalse(evaluator.CanSendMessage("p1", "p1:agent1", "p2:agent4").IsAllowed);
    }

    [TestMethod]
    public void ScopeB_PrincipalToAllAgentsOfPrincipal()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.message.allow", "p2:*") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", "p1:agent1", "p2:agent3").IsAllowed);
        Assert.IsTrue(evaluator.CanSendMessage("p1", "p1:whatever", "p2:agent9").IsAllowed);
        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p2:agent3").IsAllowed);
        Assert.IsFalse(evaluator.CanSendMessage("p1", null, "p3:agent3").IsAllowed);
        Assert.IsFalse(evaluator.CanSendMessage("p9", null, "p2:agent3").IsAllowed);
    }

    [TestMethod]
    public void ScopeC_PrincipalToNamedAgentOfAnyPrincipal()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.message.allow", "*:agent5") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p2:agent5").IsAllowed);
        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p9:agent5").IsAllowed);
        Assert.IsFalse(evaluator.CanSendMessage("p1", null, "p2:agent6").IsAllowed);
    }

    // ── Deny semantics ──

    [TestMethod]
    public void Deny_OverridesAllow()
    {
        var data = new AclSnapshotData
        {
            Grants =
            {
                Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.message.allow", "p2:*"),
                Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.message.deny", "p2:secret")
            }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p2:open").IsAllowed);
        var denied = evaluator.CanSendMessage("p1", null, "p2:secret");
        Assert.IsFalse(denied.IsAllowed);
        Assert.AreEqual(AclOutcome.Deny, denied.Outcome);
    }

    [TestMethod]
    public void Deny_BeatsImplicitSamePrincipalAllow_DisablesAgentCreation()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.create.deny", "*:*") }
        };
        var evaluator = CreateEvaluator(data);

        // p1 cannot create agents at all — even under its own principal.
        var decision = evaluator.CanCreateAgent("p1", "p1");
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AclOutcome.Deny, decision.Outcome);

        // Messaging its own agents is unaffected.
        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p1:agent1").IsAllowed);
    }

    [TestMethod]
    public void AgentCreate_ScopedToTargetPrincipal()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.create.allow", "p2:*") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanCreateAgent("p1", "p2").IsAllowed);   // granted scope
        Assert.IsTrue(evaluator.CanCreateAgent("p1", "p1").IsAllowed);   // own principal, implicit
        Assert.IsFalse(evaluator.CanCreateAgent("p1", "p3").IsAllowed);  // out of scope
    }

    // ── Groups and roles ──

    [TestMethod]
    public void DynamicAllPrincipalsGroup_MatchesAnyPrincipal()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Group, WellKnownGroups.AllPrincipals), "agent.message.allow", "system:*") }
        };
        var evaluator = CreateEvaluator(data);

        // Any principal — even one never registered — may message system agents.
        Assert.IsTrue(evaluator.CanSendMessage("brand-new-principal", null, "system:assistant").IsAllowed);
        Assert.IsFalse(evaluator.CanSendMessage("brand-new-principal", null, "p2:assistant").IsAllowed);
    }

    [TestMethod]
    public void DynamicAllAgentsGroup_MatchesAgentSubjectsOnly()
    {
        var data = new AclSnapshotData
        {
            Grants = { Grant(new AclSubject(SubjectKind.Group, WellKnownGroups.AllAgents), "agent.message.allow", "p2:*") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", "p1:agent1", "p2:agent3").IsAllowed);
        // A principal acting without an agent is not a member of all-agents.
        Assert.IsFalse(evaluator.CanSendMessage("p1", null, "p2:agent3").IsAllowed);
    }

    [TestMethod]
    public void StoredGroupMembership_GrantsApply()
    {
        var data = new AclSnapshotData
        {
            Groups =
            {
                new AclGroup { Name = "partners", Members = { new GroupMember(SubjectKind.Principal, "p1") } }
            },
            Grants = { Grant(new AclSubject(SubjectKind.Group, "partners"), "agent.message.allow", "p2:*") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanSendMessage("p1", null, "p2:agent3").IsAllowed);
        Assert.IsFalse(evaluator.CanSendMessage("p9", null, "p2:agent3").IsAllowed);
    }

    [TestMethod]
    public void RoleGrants_ApplyToDirectlyAssignedPrincipal()
    {
        var data = new AclSnapshotData
        {
            Principals = { new AclPrincipal { Handle = "alice", Roles = { "surface:admin" } } },
            Roles =
            {
                new AclRole
                {
                    Name = "surface:admin",
                    Grants = { new PermissionGrant { Permission = "surface.adminview.allow", Resource = "*:*" } }
                }
            }
        };
        var evaluator = CreateEvaluator(data);

        // App-defined (addon) permission checked via the generic evaluate path.
        var adminCheck = evaluator.Evaluate(new AclSubjectContext("alice", null), AclAction.Parse("surface.adminview"), "*:*");
        Assert.IsTrue(adminCheck.IsAllowed);

        var nonAdminCheck = evaluator.Evaluate(new AclSubjectContext("bob", null), AclAction.Parse("surface.adminview"), "*:*");
        Assert.IsFalse(nonAdminCheck.IsAllowed);
    }

    [TestMethod]
    public void RoleGrants_ApplyViaGroupMembership()
    {
        var data = new AclSnapshotData
        {
            Principals = { new AclPrincipal { Handle = "carol" } },
            Groups =
            {
                new AclGroup
                {
                    Name = "ops-team",
                    Members = { new GroupMember(SubjectKind.Principal, "carol") },
                    Roles = { "ops-reader" }
                }
            },
            Roles =
            {
                new AclRole
                {
                    Name = "ops-reader",
                    Grants = { new PermissionGrant { Permission = "agent.read.allow", Resource = "*:*" } }
                }
            }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanRead("carol", "p2:agent3").IsAllowed);
        Assert.IsFalse(evaluator.CanRead("mallory", "p2:agent3").IsAllowed);
    }

    [TestMethod]
    public void GroupResourcePattern_ScopesTargetsByGroup()
    {
        var data = new AclSnapshotData
        {
            Groups =
            {
                new AclGroup { Name = "tenants", Members = { new GroupMember(SubjectKind.Principal, "t1") } }
            },
            Grants = { Grant(new AclSubject(SubjectKind.Principal, "p1"), "agent.create.allow", "group:tenants:*") }
        };
        var evaluator = CreateEvaluator(data);

        Assert.IsTrue(evaluator.CanCreateAgent("p1", "t1").IsAllowed);
        Assert.IsFalse(evaluator.CanCreateAgent("p1", "not-a-tenant").IsAllowed);
    }
}
