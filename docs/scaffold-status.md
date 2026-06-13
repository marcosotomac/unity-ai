# Scaffold Status

Current status: the repository has a working MCP-to-Unity control plane with observe, act, persistent job, rollback, and verification capabilities.

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
- Unity bridge exposes detailed GameObject/component inspection and atomic scene batches with automatic Undo rollback on failure.
- The first act capability persists audit events to `UnityAIArtifacts/Audit/events.jsonl`.
- The first act capability requires explicit `confirm: true` for scene mutation.
- Narrow Unity Undo verification is available through `unity.editor.undo_last_operation` for the first create→undo flow.
- Act and rollback responses plus persisted audit events include request/correlation IDs.
- Mutating `unity.editor.*` routes require a local bridge token.
- Live capability list is aligned with the currently routed observe/validate/act tools.
- `unity.console.diagnose` classifies console entries as compiler errors, runtime exceptions, warnings, import errors, or unknown and returns safe next-action guidance without mutating project state.
- `unity.console.plan_fix` derives conservative read-only fix plans from diagnostics and always requires confirmation before any later apply step.
- MCP client → MCP server → Unity bridge → Unity Editor API e2e verification passes for initial observe/validate tools and the authenticated first act mutation.
- Persistent jobs cover Edit/Play tests, Play Mode transitions, compilation waits, Android builds, package changes, and Meta XR setup.
- Durable checkpoints hash project files and can restore files that changed or remove assets that did not exist at checkpoint time.
- Asset authoring covers shaders, materials, animation clips, WAV generation, and audio importer settings.
- Prefab management covers save, variants, prefab-content edits, and apply/revert overrides.
- Android/Quest validation and Meta OpenXR configuration are routed through MCP.

## Not verified yet

- A real Android/Quest build on a machine with Android Build Support and SDK/NDK/JDK installed.
- Meta OpenXR configuration against a production project and Quest hardware.
- Uniform persisted audit events for every newer mutation family.

## Known risks

| Area | Risk | Next action |
|------|------|-------------|
| Local bridge | Uses a simple HTTP bridge on localhost before a richer transport is chosen. | Validate it inside Unity, then decide whether to keep HTTP or move to WebSocket/named pipes. |
| Mutating actions | New operation families have confirmation and checkpoints, but audit-event shape is not yet uniform. | Add one shared mutation envelope and audit writer across all operations. |
| Console logs | Console count reading uses Unity internal `LogEntries` reflection, which can vary by Unity version. | Keep graceful fallback and replace with a version-tolerant adapter after Unity testing. |
| Console diagnostics | Structured diagnostics and fix plans depend on Unity's internal console entry shape when available and fall back to captured runtime logs. | Keep fixture-driven compiler/runtime/warning e2e coverage before adding confirmed apply. |
| Headless screenshots | Unity `-nographics` may return a placeholder camera frame. | Use graphical Editor runs for scene-level baselines; CI still verifies PNG readiness and deterministic diff behavior. |
| Scene View | Scene capture requires an active Scene View camera. | Return a structured unavailable state instead of throwing through the bridge. |
| Quest builds | CI may not include Android Build Support or Meta packages. | Keep validation deterministic and add a dedicated Quest build runner. |

## Next milestone

Advance production hardening:

1. unify audit events and permission enforcement across all mutation families
2. run APK/AAB generation on a Unity installation with Android Build Support
3. install, launch, and smoke-test on Quest hardware

Use:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:mcp-unity-e2e
```
