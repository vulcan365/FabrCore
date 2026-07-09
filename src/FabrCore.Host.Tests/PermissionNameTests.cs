using FabrCore.Core.Acl;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class PermissionNameTests
{
    [TestMethod]
    public void Parse_ValidAllowName_Succeeds()
    {
        var name = PermissionName.Parse("agent.message.allow");

        Assert.AreEqual("agent", name.Entity);
        Assert.AreEqual("message", name.Behavior);
        Assert.AreEqual(PermissionEffect.Allow, name.Effect);
        Assert.AreEqual("agent.message.allow", name.Value);
        Assert.AreEqual("agent.message", name.Action.ToString());
    }

    [TestMethod]
    public void Parse_ValidDenyName_Succeeds()
    {
        var name = PermissionName.Parse("agent.create.deny");
        Assert.AreEqual(PermissionEffect.Deny, name.Effect);
    }

    [TestMethod]
    public void Parse_AppDefinedEntity_Succeeds()
    {
        var name = PermissionName.Parse("surface.adminview.allow");
        Assert.AreEqual("surface", name.Entity);
        Assert.IsFalse(name.IsReservedEntity);
    }

    [TestMethod]
    public void Parse_ReservedEntity_IsFlagged()
    {
        Assert.IsTrue(PermissionName.Parse("agent.message.allow").IsReservedEntity);
        Assert.IsTrue(PermissionName.Parse("acl.manage.allow").IsReservedEntity);
    }

    [DataTestMethod]
    [DataRow("agent.message")]                 // missing effect
    [DataRow("agent.message.allow.extra")]     // too many segments
    [DataRow("agent.message.approve")]         // invalid effect
    [DataRow("Agent.Message.Allow")]           // uppercase
    [DataRow("agent..allow")]                  // empty segment
    [DataRow("")]
    public void TryParse_InvalidNames_Fail(string value)
    {
        Assert.IsFalse(PermissionName.TryParse(value, out _));
        Assert.ThrowsExactly<FormatException>(() => PermissionName.Parse(value));
    }

    [TestMethod]
    public void AclAction_Parse_RoundTrips()
    {
        var action = AclAction.Parse("surface.adminview");
        Assert.AreEqual("surface.adminview", action.ToString());
        Assert.AreEqual("surface.adminview.allow", action.Allow.Value);
        Assert.AreEqual("surface.adminview.deny", action.Deny.Value);
    }

    [TestMethod]
    public void AclAction_Parse_InvalidShape_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() => AclAction.Parse("agent.message.allow"));
        Assert.ThrowsExactly<FormatException>(() => AclAction.Parse("agent"));
    }

    [TestMethod]
    public void AclSubject_Parse_AllKinds()
    {
        Assert.AreEqual(SubjectKind.Principal, AclSubject.Parse("principal:alice").Kind);
        Assert.AreEqual("alice:agent1", AclSubject.Parse("agent:alice:agent1").Selector);
        Assert.AreEqual(SubjectKind.Role, AclSubject.Parse("role:ops").Kind);
        Assert.AreEqual(SubjectKind.Group, AclSubject.Parse("group:tenants").Kind);
        Assert.ThrowsExactly<FormatException>(() => AclSubject.Parse("agent:bare-handle"));
        Assert.ThrowsExactly<FormatException>(() => AclSubject.Parse("unknown:alice"));
    }
}

[TestClass]
public sealed class HandleScopeMatcherTests
{
    private static bool NoGroups(string group, string principal) => false;

    [DataTestMethod]
    [DataRow("p2:agent3", "p2", "agent3", true)]     // exact
    [DataRow("p2:agent3", "p2", "agent4", false)]
    [DataRow("p2:agent3", "p3", "agent3", false)]
    [DataRow("p2:*", "p2", "anything", true)]        // all agents of principal
    [DataRow("p2:*", "p3", "anything", false)]
    [DataRow("*:agent5", "p1", "agent5", true)]      // named agent under any principal
    [DataRow("*:agent5", "p9", "agent5", true)]
    [DataRow("*:agent5", "p1", "agent6", false)]
    [DataRow("*:*", "anyone", "anything", true)]     // everything
    [DataRow("auto*:*", "automation-7", "x", true)]  // prefix wildcard per segment
    [DataRow("auto*:*", "manual-1", "x", false)]
    [DataRow("p2:report*", "p2", "reporter", true)]
    [DataRow("P2:AGENT3", "p2", "agent3", true)]     // case-insensitive
    public void Matches_PatternMatrix(string pattern, string principal, string agent, bool expected)
    {
        Assert.AreEqual(expected, HandleScopeMatcher.Matches(pattern, principal, agent, NoGroups));
    }

    [TestMethod]
    public void Matches_GroupReference_ResolvesViaCallback()
    {
        static bool InTenants(string group, string principal)
            => group == "tenants" && principal is "t1" or "t2";

        Assert.IsTrue(HandleScopeMatcher.Matches("group:tenants:*", "t1", "agent1", InTenants));
        Assert.IsTrue(HandleScopeMatcher.Matches("group:tenants:agent1", "t2", "agent1", InTenants));
        Assert.IsFalse(HandleScopeMatcher.Matches("group:tenants:*", "outsider", "agent1", InTenants));
        Assert.IsFalse(HandleScopeMatcher.Matches("group:tenants:agent1", "t1", "agent2", InTenants));
    }

    [TestMethod]
    public void Matches_MissingAgentSegment_MeansAllAgents()
    {
        Assert.IsTrue(HandleScopeMatcher.Matches("p2", "p2", "anything", NoGroups));
    }
}
