using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Identity;

namespace FabrCore.Surface.Components;

public sealed class SurfaceChatLinkCreateAgentContext
{
    public required FabrCore.Surface.Identity.SurfacePrincipalContext Principal { get; init; }

    public required ISurfacePrincipalContext PrincipalContext { get; init; }

    public required string AgentAlias { get; init; }

    public required string AgentHandle { get; init; }

    public required SurfaceWorkspaceService Workspace { get; init; }
}
