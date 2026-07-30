namespace FabrCore.Surface.Ai.Swarm;

public sealed record SurfaceSwarmSquadRouteResult(
    bool Success,
    string TargetHandle,
    string Message,
    string? Mention,
    string? Error);

public static class SurfaceSwarmSquadRouteParser
{
    public static SurfaceSwarmSquadRouteResult Resolve(SurfaceSwarmSquad squad, string text)
    {
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('@'))
        {
            return new SurfaceSwarmSquadRouteResult(true, squad.OrchestratorHandle, trimmed, null, null);
        }

        var splitAt = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var mention = splitAt < 0 ? trimmed[1..] : trimmed[1..splitAt];
        var body = splitAt < 0 ? string.Empty : trimmed[splitAt..].Trim();

        if (string.IsNullOrWhiteSpace(mention))
        {
            return new SurfaceSwarmSquadRouteResult(false, squad.OrchestratorHandle, trimmed, null, "Type an agent name after @.");
        }

        if (string.Equals(mention, "orchestrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mention, "swarm", StringComparison.OrdinalIgnoreCase))
        {
            return new SurfaceSwarmSquadRouteResult(true, squad.OrchestratorHandle, body, mention, null);
        }

        if (string.Equals(mention, "planner", StringComparison.OrdinalIgnoreCase))
        {
            return new SurfaceSwarmSquadRouteResult(true, squad.PlannerHandle, body, mention, null);
        }

        if (string.Equals(mention, "supervisor", StringComparison.OrdinalIgnoreCase))
        {
            return new SurfaceSwarmSquadRouteResult(true, squad.SupervisorHandle, body, mention, null);
        }

        if (string.Equals(mention, "verifier", StringComparison.OrdinalIgnoreCase))
        {
            return new SurfaceSwarmSquadRouteResult(true, squad.VerifierHandle, body, mention, null);
        }

        var agent = squad.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, mention, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Handle, mention, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ShortHandle(candidate.Handle), mention, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            return new SurfaceSwarmSquadRouteResult(
                false,
                squad.OrchestratorHandle,
                body,
                mention,
                $"No agent named '@{mention}' exists in this squad.");
        }

        return new SurfaceSwarmSquadRouteResult(true, agent.Handle, body, mention, null);
    }

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
