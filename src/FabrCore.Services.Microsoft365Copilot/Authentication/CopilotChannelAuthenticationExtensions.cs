using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Configures JWT bearer validation for tokens that Azure Bot Service (and Entra, for the
/// Microsoft 365 / Teams channels) presents to the messaging routes. The scheme accepts a token
/// for any exposed agent's bot; <see cref="CopilotAgentRouting"/> then holds each route to its own.
/// Registered under a dedicated scheme + policy so it never interferes with any authentication
/// the FabrCore host itself sets up.
/// </summary>
internal static class CopilotChannelAuthenticationExtensions
{
    private const string AzureBotServiceOpenIdMetadataUrl =
        "https://login.botframework.com/v1/.well-known/openidconfiguration";

    // Well-known first-party tenants that issue Bot Framework / Microsoft service tokens.
    private const string BotFrameworkTenantId = "d6d49420-f39b-4df7-a1dc-d59a935871db";
    private const string MicrosoftServicesTenantId = "f8cdef31-a31e-4b4a-93e4-5f571e91255a";

    public static IServiceCollection AddCopilotChannelAuthentication(
        this IServiceCollection services,
        Microsoft365CopilotOptions options,
        IReadOnlyList<CopilotAgent> agents)
    {
        var audiences = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.ClientId))
            .Select(agent => agent.ClientId!)
            .Concat(options.TokenValidation.Audiences)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tenants = agents
            .Select(agent => agent.TenantId)
            .Append(options.TenantId)
            .Where(tenant => !string.IsNullOrWhiteSpace(tenant))
            .Select(tenant => tenant!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issuers = BuildValidIssuers(options, tenants);

        var metadataManagers = BuildMetadataManagers(options, tenants);

        services.AddAuthentication()
            .AddJwtBearer(Microsoft365CopilotDefaults.AuthenticationScheme, jwt =>
            {
                jwt.SaveToken = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = issuers,
                    ValidateAudience = true,
                    ValidAudiences = audiences,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5),
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, kid, _) => ResolveSigningKeys(kid, metadataManagers),
                };
            });

        services.AddAuthorization(auth =>
            auth.AddPolicy(Microsoft365CopilotDefaults.AuthorizationPolicy, policy => policy
                .AddAuthenticationSchemes(Microsoft365CopilotDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()));

        return services;
    }

    private static List<string> BuildValidIssuers(Microsoft365CopilotOptions options, List<string> tenants)
    {
        var issuers = new List<string>
        {
            "https://api.botframework.com",
            $"https://sts.windows.net/{BotFrameworkTenantId}/",
            $"https://login.microsoftonline.com/{BotFrameworkTenantId}/v2.0",
            $"https://sts.windows.net/{MicrosoftServicesTenantId}/",
            $"https://login.microsoftonline.com/{MicrosoftServicesTenantId}/v2.0",
        };

        foreach (var tenant in tenants)
        {
            issuers.Add($"https://sts.windows.net/{tenant}/");
            issuers.Add($"https://login.microsoftonline.com/{tenant}/v2.0");
        }

        issuers.AddRange(options.TokenValidation.ValidIssuers);
        return issuers;
    }

    private static ConfigurationManager<OpenIdConnectConfiguration>[] BuildMetadataManagers(
        Microsoft365CopilotOptions options,
        List<string> tenants)
    {
        var azureBotServiceUrl = options.TokenValidation.AzureBotServiceOpenIdMetadataUrl
            ?? AzureBotServiceOpenIdMetadataUrl;

        var managers = new List<ConfigurationManager<OpenIdConnectConfiguration>> { CreateManager(azureBotServiceUrl) };
        if (options.TokenValidation.OpenIdMetadataUrl is { } entraUrl)
        {
            managers.Add(CreateManager(entraUrl));
        }
        else
        {
            foreach (var tenant in tenants.Count > 0 ? tenants : ["botframework.com"])
            {
                managers.Add(CreateManager($"https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration"));
            }
        }

        return [.. managers];

        static ConfigurationManager<OpenIdConnectConfiguration> CreateManager(string url) =>
            new(url, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());
    }

    private static IEnumerable<SecurityKey> ResolveSigningKeys(
        string? kid,
        ConfigurationManager<OpenIdConnectConfiguration>[] managers)
    {
        var keys = new List<SecurityKey>();
        foreach (var manager in managers)
        {
            try
            {
                var config = manager.GetConfigurationAsync(CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                keys.AddRange(config.SigningKeys);
            }
            catch
            {
                // One metadata endpoint being unreachable must not block keys from the other;
                // the ConfigurationManager retries/refreshes on the next request.
            }
        }

        if (!string.IsNullOrEmpty(kid))
        {
            var matching = keys.Where(k => k.KeyId == kid).ToList();
            if (matching.Count > 0)
            {
                return matching;
            }
        }

        return keys;
    }
}
