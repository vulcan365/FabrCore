using FabrCore.Host.Services;
using FabrCore.Host.Testing;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests.A2A;

/// <summary>
/// Thin adapter over the shipped <see cref="FabrCoreA2ATestHost"/>.
/// </summary>
/// <remarks>
/// The host's own A2A tests run through the same package an application consumes, using only its
/// public surface. That is deliberate: if the shipped seam cannot express these tests, it cannot
/// express a consumer's either, and this suite is where that gets caught.
/// </remarks>
internal static class A2ATestHost
{
    /// <summary>The agent type the fake registry advertises, matching the discovery fixtures.</summary>
    internal const string BotanicalDescription = "Answers questions about plants and botany.";

    internal static Task<FabrCoreA2ATestHost> StartAsync(
        Dictionary<string, string?> configuration,
        FakeFabrCoreAgentService? agentService = null,
        bool useRealRegistry = false,
        IFabrCoreSkillCatalogService? skillCatalog = null)
        => FabrCoreA2ATestHost.StartAsync(
            configuration,
            agentService,
            useRealRegistry
                // Scans this assembly's [AgentAlias] fixtures, exercising the real registry
                // including its [FabrCoreHidden] filtering.
                ? FabrCoreA2ATestHost.RegistryFor(typeof(A2ATestHost).Assembly)
                : new FakeFabrCoreRegistry()
                    .WithAgentType("botanical-agent", BotanicalDescription, "plants,botany"),
            skillCatalog is null
                ? null
                : services => services.AddSingleton(skillCatalog));
}
