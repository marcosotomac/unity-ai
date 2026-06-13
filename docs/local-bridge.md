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

The Editor window generates a bridge token. MCP servers must send that token through the `x-unity-ai-bridge-token` header for every mutating route. The bridge token and enabled state survive Unity domain reloads for the current Editor session.

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
- `POST /capabilities/unity.scene.inspect_game_object`
- `POST /capabilities/unity.scene.upsert_game_object`
- `POST /capabilities/unity.scene.batch`
- `POST /capabilities/unity.prefabs.list`
- `POST /capabilities/unity.prefab.inspect`
- `POST /capabilities/unity.asset.dependencies`
- `POST /capabilities/unity.scripts.list`
- `POST /capabilities/unity.assemblies.list`
- `POST /capabilities/unity.packages.list`
- `POST /capabilities/unity.project.settings.inspect`
- `POST /capabilities/unity.project.settings.update`
- `POST /capabilities/unity.packages.change`
- `POST /capabilities/unity.jobs.get|list|cancel`
- `POST /capabilities/unity.tests.run`
- `POST /capabilities/unity.playmode.status|control`
- `POST /capabilities/unity.compilation.status|wait`
- `POST /capabilities/unity.build.validate_android_quest`
- `POST /capabilities/unity.build.android`
- `POST /capabilities/unity.assets.author`
- `POST /capabilities/unity.prefab.manage`
- `POST /capabilities/unity.checkpoints.create|list|restore|delete`
- `POST /capabilities/unity.vision.capture`
- `POST /capabilities/unity.vision.compare`
- `POST /capabilities/unity.meta_xr.validate_setup`
- `POST /capabilities/unity.meta_xr.configure`
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
- `unity.scene.inspect_game_object`
- `unity.scene.upsert_game_object`
- `unity.scene.batch`
- `unity.prefabs.list`
- `unity.prefab.inspect`
- `unity.asset.dependencies`
- `unity.scripts.list`
- `unity.assemblies.list`
- `unity.packages.list`
- `unity.project.settings.inspect`
- `unity.project.settings.update`
- `unity.packages.change`
- `unity.jobs.get`, `unity.jobs.list`, `unity.jobs.cancel`
- `unity.tests.run`
- `unity.playmode.status`, `unity.playmode.control`
- `unity.compilation.status`, `unity.compilation.wait`
- `unity.build.validate_android_quest`, `unity.build.android`
- `unity.assets.author`
- `unity.prefab.manage`
- `unity.checkpoints.create`, `unity.checkpoints.list`, `unity.checkpoints.restore`, `unity.checkpoints.delete`
- `unity.vision.capture`
- `unity.vision.compare`
- `unity.meta_xr.validate_setup`
- `unity.meta_xr.configure`
- `unity.editor.create_empty_game_object`
- `unity.editor.undo_last_operation`

## Current limitations

- This bridge is local-only and intended for development.
- It does not yet enforce a uniform fine-grained runtime permission/audit model.
- It exposes narrow compatibility operations plus broad declarative scene authoring through `unity.scene.batch`.
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
- Scene batches use isolated Undo groups and automatically revert the complete batch when an operation fails or cannot be verified.
- Scene authoring intentionally does not expose arbitrary C# execution or reflective method invocation.
- Act and rollback responses include `requestId` and `correlationId`; persisted audit events include the same IDs.
- Durable checkpoints live under `UnityAIArtifacts/Checkpoints`; job records live under `Library/UnityAIControlPlane/Jobs`.
- Unity API work is marshaled back to the Editor main thread.
- Scene and Game camera capture writes PNGs synchronously and verifies file existence, decoding, dimensions, byte length, and SHA-256 before returning `ready: true`.
- `unity.vision.compare` only accepts artifacts under `UnityAIArtifacts/Screenshots`, emits normalized pixel metrics, and can generate a verified diff PNG.
- In Unity `-nographics` mode, camera rendering may be a stable placeholder frame. CI validates artifact readiness separately from comparison logic by using deterministic image fixtures.
- Scene View capture returns an unavailable result if no active Scene View camera exists.
