using Microsoft.Agents.Builder;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Maps the Microsoft 365 user on an inbound activity to a FabrCore principal handle.
/// Replace the default implementation by registering your own singleton before calling
/// <c>AddMicrosoft365Copilot</c> when you need custom identity mapping (for example, looking the
/// user up in your own directory).
/// </summary>
public interface ICopilotPrincipalResolver
{
    /// <summary>
    /// Resolves the FabrCore principal handle for the user behind the current turn.
    /// Returns null when the user cannot be mapped (the bridge then refuses the message).
    /// </summary>
    /// <param name="turnContext">The current Agents SDK turn.</param>
    /// <param name="userAccessToken">
    /// The user's Entra access token when user authorization (SSO) is configured; otherwise null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> ResolvePrincipalHandleAsync(
        ITurnContext turnContext,
        string? userAccessToken,
        CancellationToken cancellationToken);
}
