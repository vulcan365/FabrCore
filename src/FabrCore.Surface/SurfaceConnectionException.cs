namespace FabrCore.Surface;

/// <summary>Which step of reaching an agent failed, so a page can say what to do about it.</summary>
public enum SurfaceConnectionProblem
{
    /// <summary>The app has no host to connect to (no address configured).</summary>
    NotConfigured,

    /// <summary>The host couldn't be reached, or didn't open a connection in time.</summary>
    Unreachable,

    /// <summary>The host refused the app's credentials, or the principal's access.</summary>
    Refused,

    /// <summary>The host doesn't have the agent's type, or the agent isn't set up there.</summary>
    AgentMissing
}

/// <summary>
/// Thrown by an <see cref="ISurfacePrincipalContext"/> when it can't reach the host or an agent. <see cref="Problem"/> says which step
/// failed; the message says it in plain words, and pages may replace it with their own.
/// </summary>
public sealed class SurfaceConnectionException : Exception
{
    public SurfaceConnectionException(SurfaceConnectionProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public SurfaceConnectionProblem Problem { get; }
}
