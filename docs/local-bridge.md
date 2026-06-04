# Local Bridge

The first bridge connects the MCP server to the Unity Editor plugin over localhost HTTP.

```text
MCP client
  ↓ stdio
apps/mcp-server
  ↓ HTTP localhost
Unity Editor plugin
  ↓ Unity Editor APIs
Unity project
```

## Defaults

| Setting | Value |
|---------|-------|
| Unity bridge URL | `http://127.0.0.1:39071` |
| MCP env override | `UNITY_AI_BRIDGE_URL` |
| Mutating route token | `UNITY_AI_BRIDGE_TOKEN` |
| Timeout override | `UNITY_AI_BRIDGE_TIMEOUT_MS` |

## Unity side

Open the Editor window:

```text
Tools → Unity AI → Control Plane
```

Then click:

```text
Start Local Bridge
```

The Editor window generates a bridge token. MCP servers must send that token through the `x-unity-ai-bridge-token` header for mutating `unity.editor.*` routes.

The MCP server only attaches the token when `UNITY_AI_BRIDGE_URL` points to `http://127.0.0.1`, `http://localhost`, or `http://[::1]`.
Trailing slashes are normalized by the MCP bridge client.

The bridge handles:

- `POST /capabilities/unity.project.inspect`
- `POST /capabilities/unity.console.read`
- `POST /capabilities/unity.console.diagnose`
- `POST /capabilities/unity.console.plan_fix`
- `POST /capabilities/unity.assets.list`
- `POST /capabilities/unity.scenes.list`
- `POST /capabilities/unity.scene.inspect`
- `POST /capabilities/unity.prefabs.list`
- `POST /capabilities/unity.prefab.inspect`
- `POST /capabilities/unity.asset.dependencies`
- `POST /capabilities/unity.scripts.list`
- `POST /capabilities/unity.assemblies.list`
- `POST /capabilities/unity.packages.list`
- `POST /capabilities/unity.project.settings.inspect`
- `POST /capabilities/unity.vision.capture`
- `POST /capabilities/unity.meta_xr.validate_setup`
- `POST /capabilities/unity.editor.create_empty_game_object`
- `POST /capabilities/unity.editor.undo_last_operation`

## MCP side

The MCP server runs over stdio and exposes:

- `unity.capabilities.list`
- `unity.project.inspect`
- `unity.console.read`
- `unity.console.diagnose`
- `unity.console.plan_fix`
- `unity.assets.list`
- `unity.scenes.list`
- `unity.scene.inspect`
- `unity.prefabs.list`
- `unity.prefab.inspect`
- `unity.asset.dependencies`
- `unity.scripts.list`
- `unity.assemblies.list`
- `unity.packages.list`
- `unity.project.settings.inspect`
- `unity.vision.capture`
- `unity.meta_xr.validate_setup`
- `unity.editor.create_empty_game_object`
- `unity.editor.undo_last_operation`

## Current limitations

- This bridge is local-only and intended for development.
- It does not enforce the full permission/audit/rollback model yet.
- It currently exposes two narrow mutating routes: `unity.editor.create_empty_game_object` and `unity.editor.undo_last_operation`.
- Broader mutating `unity.editor.*` routes are planned but not enabled yet.
- Mutating routes require a local bridge token.
- The token is only a local development safety gate, not a complete permission model.
- The first mutating route returns structured `auditEvents`, `verificationSignals`, and `verificationStatus` in addition to human-readable summary strings.
- Console diagnostics are read-only and return category, severity, Unity-relative file/line hints when available, likely root cause, and next safe action.
- Console fix planning is read-only and derives conservative plans from diagnostics with `canAutoApply: false` and `requiresConfirmationBeforeApply: true`.
- Real scene mutations include the `scene_mutation_verified` signal only after post-action observation confirms the root object count changed.
- Audit events are persisted as JSONL at `UnityAIArtifacts/Audit/events.jsonl` in the Unity project root.
- Audit events include `write_audit_log` as an explicit side effect when persisted.
- Scene mutation requires `confirm: true`; otherwise the tool returns `verificationStatus: "needs_confirmation"` and does not mutate the scene.
- Narrow rollback support uses `unity.editor.undo_last_operation` and requires `confirm: true` before performing Unity Undo.
- Act and rollback responses include `requestId` and `correlationId`; persisted audit events include the same IDs.
- Current rollback verification is scoped to the controlled e2e create→undo path; production-safe rollback still needs explicit checkpoints.
- Unity API work is marshaled back to the Editor main thread.
- Game View screenshot capture can complete asynchronously; artifact readiness checks are still needed.
- Scene View capture returns an unavailable result if no active Scene View camera exists.
