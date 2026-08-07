# FabrCore WebSocket v2

`/ws` is the authenticated live principal connection for FabrCore. It complements the Host HTTP APIs; it does not provision agents or apply Blueprints.

## Connect

1. Authenticate to the Host using the application's configured ASP.NET Core authentication.
2. `POST /fabrcoreapi/ws/ticket`. The Host resolves `nameidentifier`, then `oid`, then `sub`, normalizes it to a handle, and returns a single-use ticket valid for 30 seconds.
3. Open `/ws` with both subprotocols `fabrcore.v2` and `fabrcore.ticket.<ticket>`. The server echoes only `fabrcore.v2`.
4. Send `hello` first with a stable `clientId` and the last locally stored checkpoint, if any.

Browser Origins must be explicitly allowed outside Development. Requests without an Origin are treated as headless clients and remain valid. The configured System principal is never accepted.

## Envelope and operations

Every JSON frame is camel-case and has `version`, `type`, and the applicable `id`, `correlationId`, `operation`, `deliveryMode`, `sequence`, `deliveryId`, `payload`, or structured `error` fields. Frame types are `hello`, `welcome`, `request`, `response`, `delivery`, `ack`, and `gap`.

Supported operations:

- `message.send`: `deliveryMode: "async"` always calls `SendMessage`; `deliveryMode: "requestResponse"` waits for `SendAndReceiveMessage`. This choice is independent of `AgentMessage.Kind`.
- `event.send`
- `agent.reset`
- `agent.health.get`
- `agents.tracked.list`
- `agent.tracked.check`
- `agents.shared.list`

`agent.create`, legacy `createagent`, agent reconfiguration, Blueprint application, and arbitrary provisioning are unsupported. Use `/fabrcoreapi` agent/Blueprint endpoints, stored Blueprints, startup provisioning, or an administrative workflow.

The server overwrites `AgentMessage.fromHandle` and `EventMessage.source` with the authenticated principal. Bare agent targets are principal-scoped; qualified targets use the normal ACL checks.

## Delivery and replay

Agent-to-principal messages are persisted before `delivery` notification once a v2 client is registered. Delivery is ordered and at-least-once per stable `clientId`; acknowledge explicitly with an `ack` frame containing `sequence`.

Defaults are 24-hour retention, 10,000 messages per principal, 16 clients, and 24-hour inactive-client expiration. A new `clientId` begins at the current tail. A reconnect resumes after the lower of the server acknowledgement and client checkpoint, so duplicates are possible but skips are not. If retention removed required records, the server emits `gap` and the client must resynchronize through HTTP queries.

Queue saturation closes with WebSocket status 1013 instead of dropping data. Unacknowledged durable deliveries remain replayable. Mutating client requests are not retried after an indeterminate disconnect.

## .NET client

Reference `FabrCore.Client.WebSocket` and construct `FabrCoreWebSocketClient` with an authenticated `HttpClient`, Host URI, and stable `ClientId`. It obtains a fresh ticket for every reconnect, uses exponential backoff with jitter capped at 30 seconds, exposes typed operation methods and ordered `ReadDeliveriesAsync`, and persists checkpoints through `IFabrCoreWebSocketCheckpointStore`. The default checkpoint store is in-memory. `ResyncRequired` is raised for `gap` frames.
