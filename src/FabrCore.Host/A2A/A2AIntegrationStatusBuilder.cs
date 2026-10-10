using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration;
using static FabrCore.Core.CloudServer.IntegrationAdministration;

namespace FabrCore.Host.A2A;

/// <summary>
/// Turns the A2A options a host actually runs with into the status document and posture findings
/// served by the integrations administration API. Pure: it reads options and never a secret value.
/// </summary>
internal static class A2AIntegrationStatusBuilder
{
    internal static A2AIntegrationStatus Build(A2AOptions options, IReadOnlyList<A2AExposedAgent> agents, Type? taskStore)
    {
        var bindings = options.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Binding))
            .GroupBy(agent => A2AAgentCatalog.Slug(agent.Name ?? agent.AgentType ?? string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Binding, StringComparer.OrdinalIgnoreCase);
        var jwt = options.Authentication.JwtBearer;
        var status = new A2AIntegrationStatus
        {
            Enabled = options.Enabled,
            RoutePrefix = A2AAgentCatalog.NormalizeRoutePrefix(options.RoutePrefix),
            PublicBaseUrl = options.PublicBaseUrl,
            AuthenticationMode = options.Authentication.Mode.ToString(),
            // Names only. A key without a label is still counted, so the list length is trustworthy.
            ApiKeyNames = options.Authentication.Mode == A2AAuthenticationMode.ApiKey
                ? [.. options.Authentication.ApiKey.Keys.Select(key => string.IsNullOrWhiteSpace(key.Name) ? "(unnamed)" : key.Name)]
                : [],
            Authority = options.Authentication.Mode == A2AAuthenticationMode.JwtBearer ? jwt.Authority : null,
            Audiences = options.Authentication.Mode == A2AAuthenticationMode.JwtBearer
                ? [.. new[] { jwt.Audience }.Concat(jwt.ValidAudiences).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal)]
                : [],
            RequiredScopes = [.. jwt.RequiredScopes],
            RequiredRoles = [.. jwt.RequiredRoles],
            PrincipalStrategy = options.Principal.Strategy.ToString(),
            DiscoveryMode = options.Discovery.AgentTypes.ToString(),
            TaskStore = taskStore?.Name,
            Agents =
            [
                .. agents.Select(agent => new A2AIntegrationAgent
                {
                    Name = agent.Name, DisplayName = agent.DisplayName, BasePath = agent.BasePath, Source = agent.Source.ToString(),
                    AgentType = agent.AgentType, AgentHandle = agent.FixedHandle, Binding = bindings.GetValueOrDefault(agent.Name)
                })
            ]
        };
        status.Findings = Findings(options, agents.Count, taskStore);
        status.Status = Summarize(status.Findings);
        return status;
    }

    internal static List<IntegrationCheck> Findings(A2AOptions options, int publishedAgents, Type? taskStore)
    {
        if (!options.Enabled)
        {
            return
            [
                .. new[] { "authentication", "api-key-query", "required-scopes", "principal-strategy", "publication", "public-base-url", "task-store" }
                    .Select(id => Check(id, Skipped, "A2A is disabled on this host."))
            ];
        }

        var authentication = options.Authentication;
        var jwt = authentication.JwtBearer;
        return
        [
            authentication.Mode switch
            {
                A2AAuthenticationMode.None => Check("authentication", Fail,
                    "No credential is required. Anyone who can reach the endpoint can call every published agent."),
                A2AAuthenticationMode.ApiKey => Check("authentication", Warn,
                    "API keys identify the calling connection, not the person using it. Use JwtBearer with a delegated token when callers must reach their own agent."),
                _ => Check("authentication", Pass, "Callers present a validated OAuth 2.0 bearer token.")
            },
            authentication.Mode != A2AAuthenticationMode.ApiKey
                ? Check("api-key-query", Skipped, "API key authentication is not in use.")
                : string.IsNullOrWhiteSpace(authentication.ApiKey.QueryParameterName)
                    ? Check("api-key-query", Pass, "API keys are accepted in a header only.")
                    : Check("api-key-query", Warn, "API keys are accepted in a query parameter, where they are written to access logs and proxy traces."),
            authentication.Mode != A2AAuthenticationMode.JwtBearer
                ? Check("required-scopes", Skipped, "Bearer token authentication is not in use.")
                : jwt.RequiredScopes.Count == 0 && jwt.RequiredRoles.Count == 0
                    ? Check("required-scopes", Warn, "No scope or role is required. Any token issued for the audience is accepted.")
                    : Check("required-scopes", Pass, "Tokens must carry the configured scopes or roles."),
            options.Principal.Strategy switch
            {
                A2APrincipalStrategy.CanonicalEntra => Check("principal-strategy", Pass,
                    "Callers map to the Entra tenant and object identity shared with the Microsoft 365 Copilot channel."),
                A2APrincipalStrategy.Fixed => Check("principal-strategy", Warn,
                    "Every caller shares one principal, and therefore one set of agents, history, and state."),
                A2APrincipalStrategy.ContextId => Check("principal-strategy", Warn,
                    "Principals are derived from a caller-supplied conversation id, which isolates conversations but identifies no one."),
                A2APrincipalStrategy.ApiKey => Check("principal-strategy", Warn,
                    "Principals follow the API key. Everyone using the same key shares agents and state."),
                _ => Check("principal-strategy", Warn,
                    "Principals come from a single token claim without the tenant. Use CanonicalEntra to share identity with the Microsoft 365 Copilot channel.")
            },
            publishedAgents == 0 && options.Discovery.IncludeAgentHandles.Count == 0
                ? Check("publication", Warn, "A2A is enabled but publishes no agents.")
                : options.Discovery.AgentTypes != A2ADiscoveryMode.None
                    ? Check("publication", Warn,
                        $"Registry discovery is '{options.Discovery.AgentTypes}'. Agent types are published without being named in configuration, so a new agent type can be published by a code change alone.")
                    : Check("publication", Pass, "Only agents named in configuration are published."),
            string.IsNullOrWhiteSpace(options.PublicBaseUrl)
                ? Check("public-base-url", Warn, "PublicBaseUrl is not set. Agent cards advertise the URL each request arrived on, which is wrong behind a proxy that does not forward headers.")
                : !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != Uri.UriSchemeHttps
                    ? Check("public-base-url", Fail, "PublicBaseUrl must be an absolute https URL.")
                    : Check("public-base-url", Pass, "Agent cards advertise the configured public https origin."),
            taskStore is null
                ? Check("task-store", Skipped, "The task store could not be inspected.")
                : taskStore == typeof(InMemoryA2ATaskStore)
                    ? Check("task-store", Warn, "Tasks are held in process memory. They are lost on restart and are not visible to other host instances.")
                    : Check("task-store", Pass, $"Tasks are stored by {taskStore.Name}.")
        ];
    }

    private static IntegrationCheck Check(string id, string status, string message) => new() { Id = id, Status = status, Message = message };
}
