# Validation Policy

Surface validates both the envelope and the expanded Adaptive Card before rendering.

Policy fields:

- `MaxAdaptiveCardVersion`: highest accepted Adaptive Card schema version.
- `MaxPayloadBytes`: maximum serialized size for `card` and `data`.
- `MaxDepth`: maximum JSON nesting depth.
- `AllowedActionTypes`: accepted Adaptive Card action types.
- `AllowHttpUrls`: permit `http` URLs. `https` URLs are always the preferred default.
- `AllowedTargetAgents`: accepted target-agent overrides when unknown target agents are not allowed.
- `EnableDiagnostics`: adds Surface action-count and target-handle diagnostics to logs and message args.

Planner validation rejects executable actions so planned cards stay display-only. Deterministic agent-authored cards can still use executable actions, and Surface does not validate business verbs. Validation is not domain authorization. Agents and app-side handlers must still authorize and validate any command, mutation, or workflow side effect.

Diagnostics include:

- target handle used for `ui.render`
- planned action count
- validated action count
- rejected action count and reasons
- final rendered action count when reported by the browser renderer
