using FabrCore.Sdk;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

// Marker types scanned by a real FabrCoreRegistry. The registry only looks for [AgentAlias] and
// reads attributes reflectively — it never instantiates the type — so these do not need to derive
// from FabrCoreAgentProxy. That keeps the discovery tests exercising the real registry (including
// its [FabrCoreHidden] filtering) instead of a hand-rolled stand-in.

/// <summary>A fully described agent: the shape registry discovery is meant to publish.</summary>
[AgentAlias("botanical-agent")]
[System.ComponentModel.Description("Answers questions about plants and botany.")]
[FabrCoreCapabilities("plants, botany, plant care")]
[FabrCoreNote("Use for plant identification and care questions.")]
[FabrCoreNote("Do not use for landscaping quotes — use the quotes-agent instead.")]
public sealed class DiscoverableBotanicalAgent
{
}

/// <summary>Described, and used to prove include/exclude globbing.</summary>
[AgentAlias("support-agent")]
[System.ComponentModel.Description("Answers questions about orders, returns, and shipping.")]
public sealed class DiscoverableSupportAgent
{
}

/// <summary>Registered but undescribed: published only in <c>All</c> mode.</summary>
[AgentAlias("internal-worker-agent")]
public sealed class UndescribedWorkerAgent
{
}

/// <summary>
/// Hidden from <c>/fabrcoreapi/discovery</c>, and therefore from A2A — the registry filters it
/// before the catalog ever sees it, so there is no second switch to forget.
/// </summary>
[AgentAlias("secret-agent")]
[System.ComponentModel.Description("Handles privileged internal workflows.")]
[FabrCoreHidden]
public sealed class HiddenAgent
{
}
