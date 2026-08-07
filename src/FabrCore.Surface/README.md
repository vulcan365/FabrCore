# FabrCore.Surface Developer Notes

This README is for developers working on the `FabrCore.Surface` library itself. It is not consumer integration documentation. Consumer-facing examples and install guidance belong in `docs/skills/fabrcore-surface`.

## Purpose

`FabrCore.Surface` is an Adaptive Cards bridge for FabrCore agents and Blazor apps.

The library owns:

- Adaptive Card Surface contracts and message types.
- Agent-side planning and `ui.render` message creation.
- Blazor-side receipt and browser rendering of Adaptive Cards.
- Adaptive Card action routing back to trusted app handlers and agents.
- Validation, policy mapping, diagnostics, and Surface user context.
- Command Center grouped-agent UX, runtime configuration, and blueprint provisioning for Surface squads.

The library does not own:

- Administrative agent operations and GraphRAG pages. Those live in `FabrCore.Surface.Admin`.
- Custom Surface UI schemas or custom renderer definitions.
- Razor/HTML generation from agents.
- Business-domain action behavior.
- `FabrCore.Client` UI components.

## Project Boundaries

Keep `FabrCore.Surface` independent from `FabrCore.Client`. Do not add a project or package reference from Surface to Client.

Surface may depend on:

- `FabrCore.Core` through `FabrCore.Sdk`.
- `FabrCore.Sdk`.
- ASP.NET Core/Blazor framework APIs.
- Orleans client/streaming APIs needed for Surface client context and direct message delivery.
- `AdaptiveCards` for server-side card validation.

Business work triggered by card actions must stay behind the agent or `ISurfaceActionRegistry` implementation that owns the action verb.

## Folder Map

- `Contracts`: wire contracts such as `AdaptiveCardSurfaceEnvelope`, `AdaptiveCardActionEvent`, message constants, action type constants, routing keys, and diagnostics keys.
- `Actions`: client-side action dispatch context, dispatcher, registry contract, request, and result types.
- `Components`: Blazor receiving/rendering components and the `/surface` command center. Admin and agent-operations pages belong in `FabrCore.Surface.Admin`.
- `wwwroot`: browser renderer module and minimal styling. `adaptiveCardsSurface.js` should delegate card layout to the Adaptive Cards renderer.
- `Validation`: policy checks and Adaptive Card parsing validation.
- `Templating`: lightweight template expansion for `card` + `data`.
- `Configuration`: config file shape, policy mapping, Orleans options, and service registration helpers for producer-side Surface services.
- `Services`: producer-side planning/rendering services and message factory.
- `Ai`: built-in Surface squad, orchestration, and task runner agent runtime.
- `CommandCenter`: Blazor command center state, discovery, blueprint, transcript, preferences, and squad configuration clients shared by the base command center and optional admin package.
- `Agents` and `Plugins`: built-in Surface agent/plugin entry points.
- `Brain`: defensive extraction of fenced Adaptive Card Surface envelopes from planner output.
- `Builders`: helper APIs for producing canonical Adaptive Card action data.
- `skills`: local Codex skill documentation, references, and templates for Surface.

## Adaptive Card Contract

The only render payload is `AdaptiveCardSurfaceEnvelope`.

`AgentMessage` render messages must use:

- `MessageType = SurfaceMessageTypes.UiRender`
- `DataType = SurfaceMessageTypes.DataType`
- `Data = AdaptiveCardSurfaceEnvelope` serialized with `SurfaceJson.Options`

`SurfaceMessageTypes.DataType` is `application/vnd.fabrcore.surface.adaptive-card+json`.

Deterministic agent-authored actions that route back through FabrCore use standard Adaptive Card actions plus routing metadata in `data`:

- `Action.Execute`
- `Action.Submit`

Planner-generated cards are display-only and must not include executable actions or FabrCore action routing metadata.

Client-only Adaptive Card actions remain browser/card behavior:

- `Action.OpenUrl`
- `Action.ShowCard`
- `Action.ToggleVisibility`

## Squad Model

Surface uses `Squad` for grouped agents in UI, code, configuration, and blueprints. Do not use the older grouped-agent term when naming Surface APIs, UI labels, storage shapes, or blueprint contracts.

Reserve `AgentMessage.Channel` for the underlying FabrCore message routing field only. Surface squad code may set or read that field when routing messages, but public Surface concepts should remain `Squad`.

Squads use `SurfaceSquadType` with these values:

- `Orchestrator`
- `Task`

Blueprints provision grouped agents through the top-level `squads` extension array:

```json
{
  "name": "support-workspace",
  "squads": [
    {
      "squadType": "task",
      "name": "Ops Desk",
      "orchestratorModel": "default",
      "agents": [
        {
          "name": "data-intel",
          "agentType": "data-intel-agent",
          "role": "executor"
        }
      ]
    }
  ]
}
```

The command center blueprint builder should emit `SurfaceSquadDefinition` records. Blueprint apply results report `SquadsCreated` and `SquadsSkipped`.

## Config DX

Keep `fabrcore-surface.json` minimal. Defaults live in code:

- `maxAdaptiveCardVersion`: `1.6`
- `allowedActionTypes`: standard Adaptive Card action types supported by Surface
- `allowHttpUrls`: `false`
- empty `allowedTargetAgents`

Only add policy fields to config examples when the scenario actually needs to override defaults.

Do not add planner-required action config. If a workflow needs buttons, the owning agent should emit a deterministic card and handle the resulting `ui.action`.

## Validation Rules

Validation happens after template expansion. Keep it strict enough to protect consumers, but generic enough that domain rules stay outside this library.

Surface validation should cover:

- Envelope presence, version, and id.
- Adaptive Card JSON object shape.
- Adaptive Card type and schema version.
- Payload size and nesting depth.
- Allowed Adaptive Card action types.
- Optional target-agent restrictions.
- URL safety.
- Adaptive Cards parser compatibility.
- Planner display-only enforcement.

## Planning Rules

LLM planning must produce Adaptive Card Surface envelopes directly. Do not reintroduce a custom UI schema that then gets translated into cards.

Planner prompts should reject:

- Razor
- HTML
- JavaScript
- SQL
- arbitrary API routes
- component names

Prefer deterministic `RenderAsync` paths when an agent already knows the data and card layout.

Planner-generated cards must be display-only. They should not include `Action.Execute`, `Action.Submit`, or FabrCore routing metadata.

## Testing

For Surface-only iteration:

```powershell
dotnet test src\FabrCore.Surface.Tests\FabrCore.Surface.Tests.csproj
```

Before larger Surface refactors, also run the solution build or test suite that is relevant to the changed dependency surface.

Surface tests should cover:

- Contract serialization.
- Template expansion.
- Validation policy.
- Action routing.
- Producer-side service registration.
- Consumer-side component service registration.
- Minimal config default behavior.

## Packaging Notes

The library package should pack only runtime/library artifacts. The project currently packs:

- `fabrcore-surface.json` as a content file.

Skills are not part of the `FabrCore.Surface` package. They live under `docs/skills/fabrcore-surface` at the repository root for repository-local guidance and templates. Keep skill maintenance separate from library packaging.

## Cleanup Guardrails

When cleaning or extending Surface:

- Keep the first-class UI format as Adaptive Cards only.
- Keep action dispatch in `Actions`, not in rendering or service plumbing.
- Keep browser rendering in `wwwroot/adaptiveCardsSurface.js`.
- Keep app-specific behavior out of this library.
- Keep grouped-agent Surface concepts named `Squad`; only use `AgentMessage.Channel` when referring to the FabrCore message field.
- Keep default config examples short.
- Keep skills out of the package; maintain them under `docs/skills/fabrcore-surface`.
- Remove dead package references quickly.
- Prefer shared constants like `SurfaceActionDataKeys` over repeated string literals.
