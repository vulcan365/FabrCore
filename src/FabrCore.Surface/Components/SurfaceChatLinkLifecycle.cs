using FabrCore.Core;
using FabrCore.Surface;

namespace FabrCore.Surface.Components;

internal sealed record SurfaceChatLinkLifecycleState(
    bool IsTracked,
    bool IsReady,
    AgentHealthStatus? Health);

internal static class SurfaceChatLinkLifecycle
{
    public static async Task<SurfaceChatLinkLifecycleState> ResolveAsync(
        ISurfacePrincipalContext clientContext,
        string targetHandle,
        bool allowExternalAgent)
    {
        var isTracked = await clientContext.IsAgentTracked(targetHandle);
        if (!isTracked && !allowExternalAgent)
        {
            return new SurfaceChatLinkLifecycleState(false, false, null);
        }

        var health = await clientContext.GetAgentHealth(targetHandle);
        return new SurfaceChatLinkLifecycleState(
            isTracked,
            IsHealthy(health),
            health);
    }

    public static bool IsHealthy(AgentHealthStatus health)
        => health.IsConfigured && health.State == HealthState.Healthy;
}
