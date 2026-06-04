# Scaffold Status

Current status: the repository has a working product and architecture scaffold plus a verified first observe bridge from MCP to Unity.

## Verified

- TypeScript workspace compiles with project references.
- Unity package compiles in Unity 6000.4.9f1 via batchmode verification.
- Core protocol contracts exist for capabilities, tool requests/results, observations, action plans, audit events, and verification reports.
- MCP server uses the official TypeScript MCP SDK over stdio.
- MCP server registers initial tools for capabilities, project inspection, console reading, vision capture, and Meta XR validation.
- Unity package includes a local HTTP bridge on `http://127.0.0.1:39071/`.
- Unity bridge routes initial observe capabilities to Editor-only project inspection, console summaries, screenshot capture, and Meta XR validation.
- Unity bridge routes expanded project observability: assets list, scenes list, active scene inspection, packages list, and project settings inspection.
- Unity bridge routes deeper project observability: prefab list/inspect, asset dependencies, scripts list, and assemblies list.
- Unity bridge routes the first controlled act capability: `unity.editor.create_empty_game_object` with dry-run, structured audit events, and verification signals.
- The first act capability persists audit events to `UnityAIArtifacts/Audit/events.jsonl`.
- The first act capability requires explicit `confirm: true` for scene mutation.
- Narrow Unity Undo verification is available through `unity.editor.undo_last_operation` for the first create→undo flow.
- Act and rollback responses plus persisted audit events include request/correlation IDs.
- Mutating `unity.editor.*` routes require a local bridge token.
- Live capability list is aligned with the currently routed observe/validate/act tools.
- MCP client → MCP server → Unity bridge → Unity Editor API e2e verification passes for initial observe/validate tools and the authenticated first act mutation.

## Not verified yet

- Full permission enforcement, rollback, confirmation, and audit reporting model.

## Known risks

| Area | Risk | Next action |
|------|------|-------------|
| Local bridge | Uses a simple HTTP bridge on localhost before a richer transport is chosen. | Validate it inside Unity, then decide whether to keep HTTP or move to WebSocket/named pipes. |
| Mutating actions | One low-risk create operation and one narrow Undo operation are exposed. | Expand only after permission, confirmation, persisted audit, and correlated rollback gates exist. |
| Console logs | Console count reading uses Unity internal `LogEntries` reflection, which can vary by Unity version. | Keep graceful fallback and replace with a version-tolerant adapter after Unity testing. |
| Screenshots | Game View screenshot capture may complete after the immediate refresh call. | Add an async/polling artifact check before returning results through MCP. |
| Scene View | Scene capture requires an active Scene View camera. | Return a structured unavailable state instead of throwing through the bridge. |
| Unity package | `.meta` files are not committed yet. | Generate and commit stable `.meta` files after importing the package in Unity. |

## Next milestone

Harden the first controlled act capability:

1. expand permission gates beyond declared required permissions
2. move from inline confirmation to durable two-step approval
3. add explicit rollback checkpoints instead of relying on Unity Undo stack order

Use:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:mcp-unity-e2e
```
