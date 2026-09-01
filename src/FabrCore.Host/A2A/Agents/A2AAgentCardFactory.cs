using FabrCore.Host.A2A.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>Builds the agent card served for an exposed agent.</summary>
public interface IA2AAgentCardFactory
{
    /// <summary>
    /// Builds the card for <paramref name="agent"/>, resolving absolute URLs against the
    /// configured public base URL or, when none is configured, the incoming request, and
    /// advertising any FabrCore harness skills the agent loads.
    /// </summary>
    ValueTask<A2AAgentCard> BuildAsync(
        A2AExposedAgent agent, HttpRequest request, CancellationToken cancellationToken = default);

    /// <summary>The absolute base URL clients should use to reach this server.</summary>
    string ResolveBaseUrl(HttpRequest request);
}

internal sealed class A2AAgentCardFactory : IA2AAgentCardFactory
{
    private readonly A2AOptions _options;
    private readonly IA2AHarnessSkillResolver _harnessSkills;

    public A2AAgentCardFactory(IOptions<A2AOptions> options, IA2AHarnessSkillResolver harnessSkills)
    {
        _options = options.Value;
        _harnessSkills = harnessSkills;
    }

    public string ResolveBaseUrl(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return _options.PublicBaseUrl!.TrimEnd('/');
        }

        // No configured public URL: fall back to what the request says. Behind a reverse proxy
        // this is only correct when forwarded-headers middleware has already rewritten the
        // scheme and host, which is why PublicBaseUrl is the documented production setting.
        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }

    public async ValueTask<A2AAgentCard> BuildAsync(
        A2AExposedAgent agent, HttpRequest request, CancellationToken cancellationToken = default)
    {
        var baseUrl = ResolveBaseUrl(request);
        var agentUrl = baseUrl + agent.BasePath;

        var card = new A2AAgentCard
        {
            ProtocolVersion = A2AProtocol.Version,
            Name = agent.DisplayName,
            Description = agent.Description,
            Version = agent.Version,
            IconUrl = agent.IconUrl,
            DocumentationUrl = agent.DocumentationUrl,

            // JSON-RPC is the transport A2A clients assume when a card offers several, and it is
            // the one whose semantics we implement exactly. The HTTP+JSON routes are advertised
            // as an additional interface for clients configured against a REST-style URL.
            Url = agentUrl,
            PreferredTransport = A2ATransports.JsonRpc,
            AdditionalInterfaces = new List<A2AAgentInterface>
            {
                new() { Transport = A2ATransports.JsonRpc, Url = agentUrl },
                new() { Transport = A2ATransports.HttpJson, Url = agentUrl + "/v1" },
            },

            Capabilities = new A2AAgentCapabilities
            {
                Streaming = agent.Streaming,
                PushNotifications = false,
                StateTransitionHistory = true,
            },

            DefaultInputModes = agent.InputModes.Count > 0
                ? new List<string>(agent.InputModes)
                : new List<string> { "text/plain" },
            DefaultOutputModes = agent.OutputModes.Count > 0
                ? new List<string>(agent.OutputModes)
                : new List<string> { "text/plain" },

            Skills = agent.Skills.Select(s => new A2AAgentSkill
            {
                Id = string.IsNullOrWhiteSpace(s.Id) ? A2AAgentCatalog.Slug(s.Name) : s.Id!,
                Name = s.Name,
                Description = s.Description,
                Tags = new List<string>(s.Tags),
                Examples = s.Examples.Count > 0 ? new List<string>(s.Examples) : null,
            }).ToList(),
        };

        // A harness skill the agent has loaded is a far more concrete claim than the agent's own
        // description, so append each one as its own advertised skill.
        foreach (var skill in await _harnessSkills.ResolveAsync(agent, cancellationToken))
        {
            card.Skills.Add(new A2AAgentSkill
            {
                Id = A2AAgentCatalog.Slug(skill.Name),
                Name = skill.Name,
                Description = string.IsNullOrWhiteSpace(skill.Description)
                    ? $"The {skill.Name} skill, loaded by this agent."
                    : skill.Description,
                Tags = new List<string> { "fabrcore", "harness-skill", skill.Name },
            });
        }

        if (!string.IsNullOrWhiteSpace(_options.Provider.Organization))
        {
            card.Provider = new A2AAgentProvider
            {
                Organization = _options.Provider.Organization!,
                Url = _options.Provider.Url ?? baseUrl,
            };
        }

        ApplySecurity(card);
        return card;
    }

    private void ApplySecurity(A2AAgentCard card)
    {
        var auth = _options.Authentication;
        switch (auth.Mode)
        {
            case A2AAuthenticationMode.ApiKey:
                var inQuery = !string.IsNullOrWhiteSpace(auth.ApiKey.QueryParameterName);
                card.SecuritySchemes = new Dictionary<string, A2ASecurityScheme>
                {
                    [A2ADefaults.ApiKeySchemeName] = new()
                    {
                        Type = "apiKey",
                        Description = "Shared secret issued by the operator of this FabrCore server.",
                        Name = inQuery ? auth.ApiKey.QueryParameterName! : auth.ApiKey.HeaderName,
                        In = inQuery ? "query" : "header",
                    },
                };
                card.Security = new List<Dictionary<string, List<string>>>
                {
                    new() { [A2ADefaults.ApiKeySchemeName] = new List<string>() },
                };
                break;

            case A2AAuthenticationMode.JwtBearer:
                var jwt = auth.JwtBearer;
                var scheme = new A2ASecurityScheme
                {
                    Type = "oauth2",
                    Description = "OAuth 2.0 access token accepted as an HTTP bearer credential.",
                    Flows = new A2AOAuthFlows
                    {
                        ClientCredentials = new A2AOAuthFlow
                        {
                            TokenUrl = jwt.TokenUrl,
                            RefreshUrl = jwt.RefreshUrl,
                            Scopes = new Dictionary<string, string>(jwt.Scopes),
                        },
                    },
                };

                if (!string.IsNullOrWhiteSpace(jwt.AuthorizationUrl))
                {
                    scheme.Flows.AuthorizationCode = new A2AOAuthFlow
                    {
                        AuthorizationUrl = jwt.AuthorizationUrl,
                        TokenUrl = jwt.TokenUrl,
                        RefreshUrl = jwt.RefreshUrl,
                        Scopes = new Dictionary<string, string>(jwt.Scopes),
                    };
                }

                if (!string.IsNullOrWhiteSpace(jwt.Authority))
                {
                    scheme.Oauth2MetadataUrl =
                        jwt.Authority!.TrimEnd('/') + "/.well-known/openid-configuration";
                }

                card.SecuritySchemes = new Dictionary<string, A2ASecurityScheme>
                {
                    [A2ADefaults.BearerSchemeName] = scheme,
                };
                card.Security = new List<Dictionary<string, List<string>>>
                {
                    new() { [A2ADefaults.BearerSchemeName] = jwt.Scopes.Keys.ToList() },
                };
                break;

            case A2AAuthenticationMode.None:
            default:
                // No securitySchemes and no security: the card states plainly that anyone who can
                // reach the endpoint can call it.
                break;
        }
    }
}
