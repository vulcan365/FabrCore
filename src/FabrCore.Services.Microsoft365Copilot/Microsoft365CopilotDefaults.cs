namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Well-known names used by the Microsoft 365 Copilot addon.
/// </summary>
public static class Microsoft365CopilotDefaults
{
    /// <summary>Configuration section name in fabrcore.json / appsettings.json.</summary>
    public const string SectionName = "Microsoft365Copilot";

    /// <summary>Authentication scheme used to validate inbound Azure Bot Service / Entra channel tokens.</summary>
    public const string AuthenticationScheme = "Microsoft365CopilotBearer";

    /// <summary>Authorization policy applied to the messages endpoint.</summary>
    public const string AuthorizationPolicy = "Microsoft365Copilot";

    /// <summary>Default messaging endpoint expected by Azure Bot Service.</summary>
    public const string MessagesEndpoint = "/api/messages";

    /// <summary>Name of the synthesized Agents SDK service connection.</summary>
    public const string ServiceConnectionName = "ServiceConnection";

    /// <summary>Value stamped on <c>AgentMessage.Channel</c> for traffic that arrives through this addon.</summary>
    public const string ChannelName = "m365copilot";

    /// <summary>Route prefix for the developer app-package endpoints.</summary>
    public const string AppPackageRoutePrefix = "/m365copilot";

    /// <summary>Route prefix for the name-addressed manifest endpoint (<c>/manifests/{name}.json</c>).</summary>
    public const string ManifestsRoutePrefix = "/manifests";

    // Keys stamped onto AgentMessage.Args for every bridged message so FabrCore
    // agents and plugins can see who is talking and from where.
    public const string ArgAadObjectId = "Microsoft365Copilot:AadObjectId";
    public const string ArgTenantId = "Microsoft365Copilot:TenantId";
    public const string ArgUserName = "Microsoft365Copilot:UserName";
    public const string ArgConversationId = "Microsoft365Copilot:ConversationId";
    public const string ArgChannelId = "Microsoft365Copilot:ChannelId";
    public const string ArgLocale = "Microsoft365Copilot:Locale";
    public const string ArgActivityId = "Microsoft365Copilot:ActivityId";

    /// <summary>
    /// Arg key that carries the signed-in user's Entra access token when
    /// <c>UserAuthorization.PassUserTokenToAgent</c> is enabled.
    /// </summary>
    public const string ArgUserToken = "Microsoft365Copilot:UserToken";
}
