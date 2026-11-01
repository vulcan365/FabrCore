namespace FabrCore.Services.Microsoft365Copilot.Tests;

/// <summary>Shorthand for the agent entries most tests need.</summary>
internal static class CopilotTestAgents
{
    public const string ClientId = "11111111-2222-3333-4444-555555555555";
    public const string SecondClientId = "66666666-7777-8888-9999-000000000000";

    public static CopilotAgentEntryOptions Entry(
        string name = "assistant", string? clientId = ClientId, string agentType = "chat-agent", string? route = null) => new()
    {
        Name = name,
        Route = route,
        ClientId = clientId,
        ClientSecret = clientId is null ? null : "secret",
        AgentType = agentType,
    };

    /// <summary>Options exposing one agent on the bare <c>/api/messages</c> route.</summary>
    public static Microsoft365CopilotOptions Single(Action<Microsoft365CopilotOptions>? configure = null)
    {
        var options = new Microsoft365CopilotOptions { Agents = { Entry(route: "/api/messages") } };
        configure?.Invoke(options);
        return options;
    }

    public static IReadOnlyList<CopilotAgent> Resolve(Microsoft365CopilotOptions options) => CopilotAgentCatalog.Resolve(options);

    public static CopilotAgentCatalog Catalog(Microsoft365CopilotOptions options) => new(Resolve(options));
}
