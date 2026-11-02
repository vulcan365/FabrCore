using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FabrCore.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>Receives a person's token after they sign in, so it never has to go into an agent's messages.</summary>
public interface ICopilotUserAssertionSink
{
    /// <summary>Called on each turn that has a signed-in person's token. A failure doesn't stop the turn.</summary>
    Task SubmitAsync(CopilotAgent agent, string principalHandle, string userToken, CancellationToken cancellationToken);
}

/// <summary>
/// Hands the token to the agent's on-behalf-of connections. Does nothing on a host without Connections. Teams repeats the same token
/// each turn, so only a new one goes on: each submission is validated, and only a nearly expired grant is replaced.
/// </summary>
internal sealed class ConnectionsCopilotUserAssertionSink(IServiceProvider services) : ICopilotUserAssertionSink
{
    private const int MaxRemembered = 10_000;

    private readonly IConnectionUserAssertions? _assertions = services.GetService<IConnectionUserAssertions>();
    private readonly ConcurrentDictionary<string, string> _lastSubmitted = new(StringComparer.Ordinal);

    public async Task SubmitAsync(CopilotAgent agent, string principalHandle, string userToken, CancellationToken cancellationToken)
    {
        if (_assertions is null)
        {
            return;
        }

        var agentKey = $"{principalHandle}:{agent.Handle}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userToken)));
        if (_lastSubmitted.TryGetValue(agentKey, out var last) && last == fingerprint)
        {
            return;
        }

        await _assertions.SubmitAsync(principalHandle, agentKey, userToken, cancellationToken);

        // Only saves a repeat submission, which the connection would turn down anyway.
        if (_lastSubmitted.Count >= MaxRemembered)
        {
            _lastSubmitted.Clear();
        }
        _lastSubmitted[agentKey] = fingerprint;
    }
}
