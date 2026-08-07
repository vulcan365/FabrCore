namespace FabrCore.Surface.Ai.Squads;

public sealed record SurfaceSquadRouteResult(
    bool Success,
    string TargetHandle,
    string Message,
    string? Mention,
    string? Error);

public static class SurfaceSquadRouteParser
{
    public static SurfaceSquadRouteResult Resolve(SurfaceSquad squad, string text)
    {
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('@'))
        {
            return new SurfaceSquadRouteResult(true, squad.OrchestratorHandle, trimmed, null, null);
        }

        var splitAt = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var mention = splitAt < 0 ? trimmed[1..] : trimmed[1..splitAt];
        var body = splitAt < 0 ? string.Empty : trimmed[splitAt..].Trim();

        if (string.IsNullOrWhiteSpace(mention))
        {
            return new SurfaceSquadRouteResult(false, squad.OrchestratorHandle, trimmed, null, "Type an agent name after @.");
        }

        if (squad.SquadType == SurfaceSquadType.Task)
        {
            return new SurfaceSquadRouteResult(true, squad.OrchestratorHandle, trimmed, null, null);
        }

        if (string.Equals(mention, "orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return new SurfaceSquadRouteResult(true, squad.OrchestratorHandle, body, mention, null);
        }

        var agent = squad.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, mention, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Handle, mention, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ShortHandle(candidate.Handle), mention, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            return new SurfaceSquadRouteResult(
                false,
                squad.OrchestratorHandle,
                body,
                mention,
                $"No agent named '@{mention}' exists in this squad.");
        }

        return new SurfaceSquadRouteResult(true, agent.Handle, body, mention, null);
    }

    private static string ShortHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
    }
}
