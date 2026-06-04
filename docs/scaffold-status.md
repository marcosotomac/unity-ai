# Scaffold Status

Current status: the repository has a working product and architecture scaffold plus a verified first observe bridge from MCP to Unity.

## Verified

- TypeScript workspace compiles with project references.
- Unity package compiles in Unity 6000.4.9f1 via batchmode verification.
- Core protocol contracts exist for capabilities, tool requests/results, observations, action plans, audit events, and verification reports.
- MCP server uses the official TypeScript MCP SDK over stdio.
- MCP server registers initial tools for capabilities, project inspection, console reading/diagnostics, vision capture, and Meta XR validation.
- Unity package includes a local HTTP bridge on `http://127.0.0.1:39071/`.
- Unity bridge routes initial observe capabilities to Editor-only project inspection, console summaries/diagnostics, screenshot capture, and Meta XR validation.
- Unity bridge routes expanded project observability: assets list, scenes list, active scene inspection, packages list, and project settings inspection.
- Unity bridge routes deeper project observability: prefab list/inspect, asset dependencies, scripts list, and assemblies list.
- Unity bridge routes the first controlled act capability: `unity.editor.create_empty_game_object` with dry-run, structured audit events, and verification signals.
- The first act capability persists audit events to `UnityAIArtifacts/Audit/events.jsonl`.
- The first act capability requires explicit `confirm: true` for scene mutation.
- Narrow Unity Undo verification is available through `unity.editor.undo_last_operation` for the first create→undo flow.
- Act and rollback responses plus persisted audit events include request/correlation IDs.
- Mutating `unity.editor.*` routes require a local bridge token.
- Live capability list is aligned with the currently routed observe/validate/act tools.
- `unity.console.diagnose` classifies console entries as compiler errors, runtime exceptions, warnings, import errors, or unknown and returns safe next-action guidance without mutating project state.
- `unity.console.plan_fix` derives conservative read-only fix plans from diagnostics and always requires confirmation before any later apply step.
- MCP client → MCP server → Unity bridge → Unity Editor API e2e verification passes for initial observe/validate tools and the authenticated first act mutation.

## Not verified yet

- Full permission enforcement, rollback, confirmation, and audit reporting model.

## Known risks

| Area | Risk | Next action |
|------|------|-------------|
| Local bridge | Uses a simple HTTP bridge on localhost before a richer transport is chosen. | Validate it inside Unity, then decide whether to keep HTTP or move to WebSocket/named pipes. |
| Mutating actions | One low-risk create operation and one narrow Undo operation are exposed. | Expand only after permission, confirmation, persisted audit, and correlated rollback gates exist. |
| Console logs | Console count reading uses Unity internal `LogEntries` reflection, which can vary by Unity version. | Keep graceful fallback and replace with a version-tolerant adapter after Unity testing. |
| Console diagnostics | Structured diagnostics and fix plans depend on Unity's internal console entry shape when available and fall back to captured runtime logs. | Keep fixture-driven compiler/runtime/warning e2e coverage before adding confirmed apply. |
| Screenshots | Game View screenshot capture may complete after the immediate refresh call. | Add an async/polling artifact check before returning results through MCP. |
| Scene View | Scene capture requires an active Scene View camera. | Return a structured unavailable state instead of throwing through the bridge. |
| Unity package | `.meta` files are not committed yet. | Generate and commit stable `.meta` files after importing the package in Unity. |

## Next milestone

Advance the first diagnose/fix loop:

1. add a confirmed safe apply step for one narrow diagnostic category
2. recompile and verify console state before reporting success
3. produce before/after audit evidence

Use:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:mcp-unity-e2e
```
