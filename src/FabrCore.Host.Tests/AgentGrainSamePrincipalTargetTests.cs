using FabrCore.Host.Grains;

namespace FabrCore.Host.Tests;

/// <summary>
/// Regression tests for sender-side cross-principal classification. An agent sending to its own
/// principal handle (the bare client-delivery form ResolveTargetHandle routes to the principal's
/// stream — used e.g. by Surface UI render pushes) must count as same-principal traffic, not
/// require an agent.message grant.
/// </summary>
[TestClass]
public sealed class AgentGrainSamePrincipalTargetTests
{
    [TestMethod]
    public void BareOwnPrincipalHandle_IsSamePrincipal()
    {
        // The Copilot bridge scenario: agent "oid:copilot" pushes a UI render to principal "oid".
        Assert.IsTrue(AgentGrain.IsSamePrincipalTarget(
            "c57af63c-3b08-4c07-a2bb-a080e2fbe37d",
            "c57af63c-3b08-4c07-a2bb-a080e2fbe37d"));
    }

    [TestMethod]
    public void BareOwnPrincipalHandle_IsCaseInsensitive()
    {
        Assert.IsTrue(AgentGrain.IsSamePrincipalTarget("Demo-User", "demo-user"));
    }

    [TestMethod]
    public void QualifiedSamePrincipalAgent_IsSamePrincipal()
    {
        Assert.IsTrue(AgentGrain.IsSamePrincipalTarget("demo-user", "demo-user:crm-agent"));
    }

    [TestMethod]
    public void QualifiedCrossPrincipalAgent_IsNotSamePrincipal()
    {
        Assert.IsFalse(AgentGrain.IsSamePrincipalTarget("demo-user", "other-user:crm-agent"));
    }

    [TestMethod]
    public void BareOtherPrincipalHandle_IsNotSamePrincipal()
    {
        Assert.IsFalse(AgentGrain.IsSamePrincipalTarget("demo-user", "other-user"));
    }

    [TestMethod]
    public void SystemAgent_ToSystemPrincipal_IsSamePrincipal()
    {
        Assert.IsTrue(AgentGrain.IsSamePrincipalTarget("system", "system"));
    }
}
