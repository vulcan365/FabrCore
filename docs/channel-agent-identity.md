# Shared Teams, Microsoft 365 Copilot, and A2A identity

The adapters share principal identity, named agent bindings, and principal-authorized invocation. Azure Bot activities and A2A HTTP requests retain their own authentication and transport lifecycles.

Set `Microsoft365Copilot:Principal:Strategy` and `A2A:Principal:Strategy` to `CanonicalEntra`, with the same prefix (normally empty). A validated delegated Entra token on A2A and the corresponding user on a trusted Teams activity both resolve to:

```text
entra-{tenant-guid}-{object-guid}:assistant
```

GUIDs are normalized to lowercase, without truncation or lossy character replacement. Missing or malformed tenant/object IDs are rejected. The tenant is part of identity: a guest identity in another tenant is a different principal. Existing principal strategies retain their previous defaults; switching strategies does not move existing agent state.

Application tokens map to `app-{tenant-guid}-{object-guid}`. An API key or an OAuth client-credentials connection identifies the connection/application, not the person chatting with Copilot Studio. It cannot safely recover the user's Teams identity. Do not trust a user ID supplied in message metadata, history, context ID, or an unverified header. For user continuity, the connection must deliver a validated delegated access token with `tid`, `oid`, and `scp` for the A2A API. If the connector cannot do that, a trusted identity integration/custom resolver is still required; ordinary client credentials do not provide user continuity.

Use a named binding to prevent the adapters from provisioning different agent configurations:

```json
{
  "AgentBindings": {
    "assistant": {
      "AgentType": "crm-demo-agent",
      "Handle": "assistant",
      "Models": "default"
    }
  },
  "Microsoft365Copilot": {
    "Principal": { "Strategy": "CanonicalEntra" },
    "Agent": { "Binding": "assistant" }
  },
  "A2A": {
    "Enabled": true,
    "PublicBaseUrl": "https://agents.example.com",
    "Principal": { "Strategy": "CanonicalEntra" },
    "Agents": [ { "Name": "crm-assistant", "Binding": "assistant" } ],
    "Authentication": {
      "Mode": "JwtBearer",
      "JwtBearer": {
        "Authority": "https://login.microsoftonline.com/TENANT-ID/v2.0",
        "Audience": "A2A-API-CLIENT-ID",
        "RequiredScopes": [ "agent.invoke" ]
      }
    }
  }
}
```

The binding owns the handle, type, models, prompt, plugins, tools, and arguments. A2A defaults do not supplement a bound agent's tools or prompt. Bindings require per-principal routing: conversation/context suffixing and shared-agent handles cannot be combined with them. Set bot registration credentials separately, using user secrets or a secret store. Configure the API registration, delegated permission, consent, and connector token flow before enabling the endpoint. See the [sample configuration](../samples/FabrCore.SampleApp/fabrcore.sample.json).

Both adapters invoke `IFabrCoreAgentService.SendAndReceiveMessageAsync`, which now dispatches through the caller's `IPrincipalGrain`. Bare handles receive the principal prefix there; qualified handles are preserved and checked by the existing cross-principal ACL. The sender is stamped from the resolved principal, not from input metadata.

## A2A 1.0

The public protocol targets specification release **1.0.1**, whose negotiated wire version is **1.0**. Cards advertise `supportedInterfaces`. JSON-RPC methods are `SendMessage`, `SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`, and `SubscribeToTask`. REST paths are relative to `/a2a/{agent}`, without a `/v1` prefix:

| Method | Path |
| --- | --- |
| POST | `/message:send` |
| POST | `/message:stream` |
| GET | `/tasks` |
| GET | `/tasks/{id}` |
| POST | `/tasks/{id}:cancel` |
| GET | `/tasks/{id}:subscribe` |

Use `A2A-Version: 1.0` on calls. The specification interprets missing versions as 0.3, so this 1.0-only host rejects missing or unsupported call versions. Discovery cards can be fetched without a version header. `SendMessage` returns `{ "task": ... }` or `{ "message": ... }`. Streams contain `task`, `statusUpdate`, and `artifactUpdate` wrappers; the terminal task state closes the stream. Parts have `text`, `data`, `raw`, or `url`, without `kind`; roles/states use `ROLE_USER`, `ROLE_AGENT`, and `TASK_STATE_*`. REST success responses use `application/a2a+json`, and errors use `application/problem+json`. Set `configuration.returnImmediately` for asynchronous execution.

Task IDs are server-generated. Every get, cancel, subscribe, and list checks the creating principal, exposed agent, and caller identity, including persisted snapshots. Unowned tasks appear not found. A completed turn cannot be resumed by supplying its task ID; start a new task with the same context ID. Push notifications and extended cards are explicitly unsupported. File URLs are passed as content references and are never fetched by the bridge.

## Operational behavior

The default A2A task store and Agents SDK turn/sign-in storage are in memory. For scale-out or restart durability, register an `IA2ATaskStore` (including `ListAsync`) and Agents SDK `IStorage` before registering the adapters. Preserve the server-owned task metadata when persisting snapshots. This change does not turn Orleans agent persistence into durable A2A execution: active tasks and subscriptions are still process-local.

Cancellation/timeout stops the A2A wait and updates the task; it does not interrupt work already running inside an agent. Do not automatically retry side-effecting turns after a timeout. Persistence failures are logged and stream cleanup still runs.

`A2A:Tasks:MaxConcurrentTasks` defaults to 100 per process. Excess work receives HTTP 429 (or JSON-RPC server error `-32050`) with `Retry-After`; retained task counts and retention periods are independently configurable. Subscriber queues are bounded and disconnected subscribers are removed.

M365 app packages use manifest 1.25. Text, adaptive-card replies/submits, AI labeling, and existing proactive delivery remain supported. Attachment-only messages now receive an explicit explanation. M365 streaming reports progress and the completed reply; it is not token-by-token model streaming. Feedback/citation UX and file ingestion still require channel-specific integration.

Sources: [A2A 1.0.1 schema](https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto), [Entra token claims](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference), [Copilot Studio A2A connections](https://learn.microsoft.com/en-us/microsoft-copilot-studio/add-agent-agent-to-agent).
