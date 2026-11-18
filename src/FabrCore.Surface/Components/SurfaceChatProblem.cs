namespace FabrCore.Surface.Components;

/// <summary>What <see cref="SurfaceChat"/> shows when it can't reach its agent: a headline and what to do about it.</summary>
public sealed record SurfaceChatProblem(string Title, string Message, SurfaceConnectionProblem? Problem = null)
{
    /// <summary>Surface's own words for a failure. Pages pass <c>SurfaceChat.DescribeProblem</c> to use theirs.</summary>
    public static SurfaceChatProblem From(Exception exception, string agentName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SurfaceConnectionException connection
            ? new SurfaceChatProblem(connection.Problem switch
            {
                SurfaceConnectionProblem.NotConfigured => "Not connected to a FabrCore host",
                SurfaceConnectionProblem.Refused => "The FabrCore host refused the connection",
                SurfaceConnectionProblem.AgentMissing => $"{agentName} isn't set up yet",
                _ => "The FabrCore host couldn't be reached"
            }, connection.Message, connection.Problem)
            : new SurfaceChatProblem("The chat couldn't start", exception.Message);
    }
}
