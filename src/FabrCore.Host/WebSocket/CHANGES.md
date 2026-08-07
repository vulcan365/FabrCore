# WebSocket v2 breaking changes

The legacy raw-`AgentMessage` WebSocket contract was removed. `/ws` now requires the `fabrcore.v2` subprotocol, a one-time authenticated ticket, and a `hello` frame.

Removed behavior:

- `x-fabrcore-userhandle` and `userhandle` principal selection, except when the explicit Development-only compatibility option is enabled
- raw `AgentMessage` and `EventMessage` frames
- `MessageType: "command"` and `createagent`
- delivery-mode inference from `AgentMessage.Kind`
- `DropOldest` outbound queues

Agent provisioning remains on Host HTTP and Blueprint APIs. See `README.md` in this directory for v2 connection, operations, replay, and typed-client guidance.
