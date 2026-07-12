# Entra User SSO / OBO Reference

By default the addon needs **no sign-in**: Teams and Microsoft 365 Copilot stamp the user's Entra
identity (`From.AadObjectId`, tenant id) on every activity, and that is what maps users to
FabrCore principals. Configure user authorization only when FabrCore agents/plugins must call
downstream APIs (Microsoft Graph, your own APIs) **as the signed-in user**.

## 1. App registration (the bot's registration)

In the bot's Entra app registration:

1. **Expose an API** → set the Application ID URI to exactly `api://botid-{clientId}`
   (required format for Teams/Copilot SSO).
2. Add a scope, e.g. `defaultScopes` (admin+user consent).
3. **Authorized client applications** — add the Microsoft client ids so Teams/Copilot/Outlook can
   silently acquire the token:

   | Client | Id |
   |---|---|
   | Teams desktop/mobile | `1fec8e78-bce4-4aaf-ab1b-5451cc387264` |
   | Teams web | `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` |
   | Microsoft 365 web | `4765445b-32c6-49b0-83e6-1d93765276ca` |
   | Microsoft 365 desktop | `0ec893e0-5785-4de6-99da-4ed124e5296c` |
   | Outlook desktop / M365 mobile | `d3590ed6-52b3-4102-aeff-aad2292ab01c` |
   | Outlook web | `bc59ab01-8403-45c6-8796-ac3ef710b3e3` |
   | Outlook mobile | `27922004-5251-4030-b22d-91ecd9a37ea4` |

4. **API permissions** — add the delegated permissions the agent needs (e.g. Graph `User.Read`,
   `Mail.Read`); grant admin consent if required.

## 2. OAuth connection on the Azure Bot

Azure portal → the bot resource → *Settings → Configuration → Add OAuth Connection Settings*:

- Provider: **Azure Active Directory v2** (or *AAD v2 with Federated Credentials* for secretless).
- Client id / secret: the bot registration's.
- Tenant: your tenant id.
- **Token Exchange URL: `api://botid-{clientId}`** — this is what enables silent SSO.
- Scopes: what you want in the user token, e.g. `api://botid-{clientId}/defaultScopes` for an
  OBO-exchangeable token, or Graph scopes directly.

Note the **connection name** — it goes in the config below.

## 3. Addon configuration

The `Handlers` subsection uses the Microsoft 365 Agents SDK handler schema and is forwarded to
the SDK verbatim (`AgentApplication:UserAuthorization:Handlers`):

```json
"Microsoft365Copilot": {
  "UserAuthorization": {
    "DefaultHandlerName": "graph",
    "AutoSignIn": true,
    "PassUserTokenToAgent": true,
    "Handlers": {
      "graph": {
        "Settings": {
          "AzureBotOAuthConnectionName": "<oauth-connection-name>",
          "OBOConnectionName": "ServiceConnection",
          "OBOScopes": [ "https://graph.microsoft.com/.default" ]
        }
      }
    }
  }
}
```

- `AzureBotOAuthConnectionName` — the OAuth connection from step 2.
- `OBOConnectionName` + `OBOScopes` — optional: when both are set the SDK performs the
  on-behalf-of exchange automatically, so the turn token is already a Graph (or your-API) token.
  `ServiceConnection` is the connection the addon synthesizes from
  `TenantId`/`ClientId`/`ClientSecret`.
- `PassUserTokenToAgent` (addon-specific) — stamps the token onto
  `AgentMessage.Args["Microsoft365Copilot:UserToken"]` for every bridged message.

The generated app manifest automatically gains `webApplicationInfo`
(`id` = ClientId, `resource` = `api://botid-{clientId}`) and `token.botframework.com` in
`validDomains` whenever handlers are configured — re-download and re-upload the app package after
enabling SSO.

## 4. Consuming the token in a FabrCore agent/plugin

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    if (message.Args?.TryGetValue("Microsoft365Copilot:UserToken", out var userToken) == true)
    {
        // Call Microsoft Graph / your API with Bearer userToken — acting as the user.
    }
    ...
}
```

## Security notes

- The token flows through FabrCore messaging: it can appear in the in-process message monitor
  (payload capture) and any audit sinks. Keep `PassUserTokenToAgent` off unless agents need it,
  scope `OBOScopes` minimally, and prefer short-lived Graph tokens over exchangeable ones.
- Sign-in/turn state persists via the Agents SDK `IStorage`. `MemoryStorage` (the default) loses
  sign-in state on restart and does not work across multiple instances — register a durable
  `IStorage` before `AddMicrosoft365Copilot()` in production.
- `UserPrincipalName` principal strategy reads the UPN from this token; without user
  authorization configured it falls back to the Entra object id.
