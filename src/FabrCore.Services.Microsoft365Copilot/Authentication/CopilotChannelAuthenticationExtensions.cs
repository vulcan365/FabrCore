using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Configures JWT bearer validation for tokens that Azure Bot Service (and Entra, for the
/// Microsoft 365 / Teams channels) presents to the <c>/api/messages</c> endpoint.
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
        Microsoft365CopilotOptions options)
    {
        var tokenValidation = options.TokenValidation;

        var audiences = tokenValidation.Audiences.Count > 0
            ? tokenValidation.Audiences
            : [options.ClientId!];

        var issuers = BuildValidIssuers(options);

        var metadataManagers = BuildMetadataManagers(options);

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

    private static List<string> BuildValidIssuers(Microsoft365CopilotOptions options)
    {
        var issuers = new List<string>
        {
            "https://api.botframework.com",
            $"https://sts.windows.net/{BotFrameworkTenantId}/",
            $"https://login.microsoftonline.com/{BotFrameworkTenantId}/v2.0",
            $"https://sts.windows.net/{MicrosoftServicesTenantId}/",
            $"https://login.microsoftonline.com/{MicrosoftServicesTenantId}/v2.0",
        };

        if (!string.IsNullOrWhiteSpace(options.TenantId))
        {
            issuers.Add($"https://sts.windows.net/{options.TenantId}/");
            issuers.Add($"https://login.microsoftonline.com/{options.TenantId}/v2.0");
        }

        issuers.AddRange(options.TokenValidation.ValidIssuers);
        return issuers;
    }

    private static ConfigurationManager<OpenIdConnectConfiguration>[] BuildMetadataManagers(
        Microsoft365CopilotOptions options)
    {
        var azureBotServiceUrl = options.TokenValidation.AzureBotServiceOpenIdMetadataUrl
            ?? AzureBotServiceOpenIdMetadataUrl;

        var entraTenant = string.IsNullOrWhiteSpace(options.TenantId) ? "botframework.com" : options.TenantId;
        var entraUrl = options.TokenValidation.OpenIdMetadataUrl
            ?? $"https://login.microsoftonline.com/{entraTenant}/v2.0/.well-known/openid-configuration";

        return
        [
            CreateManager(azureBotServiceUrl),
            CreateManager(entraUrl),
        ];

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
