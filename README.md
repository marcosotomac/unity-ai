# Unity AI Control Plane

Open-source control plane for Unity and Meta XR projects where AI agents can observe project state, act through controlled Unity operations, and verify the result with logs, tests, builds, and visual feedback.

> Working thesis: AI should not only generate code for Unity. It should operate on real projects with a closed loop: **observe → act → verify**.

## Product loop

| Phase | Goal | Example signals |
|-------|------|-----------------|
| Observe | Understand the current Unity project state. | Scene/Game View screenshots, console logs, hierarchy, assets, prefabs, scripts, packages, XR/build settings, performance data. |
| Act | Apply controlled changes through Unity and Meta XR capabilities. | Editor scripts, scene edits, prefab changes, C# fixes, package/config updates, Meta XR rig setup, build/test commands. |
| Verify | Prove the change worked before reporting success. | Recompile, read logs again, run tests, validate settings, capture final screenshots, compare before/after, produce a report. |

## Core architecture

```text
AI agents / agent runners
        ↓ MCP
MCP server
        ↓ local transport
Unity Editor plugin
        ↓
Unity Editor APIs + Meta XR SDK + project assets
```

## Main components

### MCP server

Exposes project-grade tools to AI agents while defining capability contracts and routing requests. Current runtime safety includes local token gating for mutating routes, confirmation for the first scene mutation, persisted audit events, post-action verification, and narrow Unity Undo verification for the first act loop. Full permission policy, durable two-step approval, correlated rollback checkpoints, and production rollback safety are planned hardening work.

Initial tool families:

- `unity.project.*` — inspect project structure, packages, settings, scenes, scripts, and assets.
- `unity.assets.*` — list project assets with GUIDs, paths, and main asset types.
- `unity.asset.*` — inspect specific asset metadata and dependencies.
- `unity.prefabs.*` / `unity.prefab.*` — list and inspect prefab assets.
- `unity.scene.*` — inspect and modify scenes, GameObjects, prefabs, materials, cameras, lighting, and UI.
- `unity.scenes.*` — list scenes discovered in the project and Build Settings.
- `unity.scripts.*` / `unity.assemblies.*` — inspect C# scripts and Unity script assemblies.
- `unity.packages.*` — list registered Unity packages.
- `unity.vision.*` — capture Scene View/Game View screenshots and compare visual results.
- `unity.console.*` — read logs, group errors by likely root cause, and verify fixes.
- `unity.meta_xr.*` — validate and configure Meta XR SDK, OpenXR, rigs, hands, passthrough, anchors, interactions, and Quest build requirements.
- `unity.tests.*` — run Edit Mode/Play Mode tests and summarize failures.
- `unity.build.*` — validate and execute builds, especially Android/Quest targets.

### Unity Editor plugin

Runs inside Unity and provides the actual control plane over the project.

Responsibilities:

- Capture Scene View and Game View screenshots.
- Read Unity Console logs and compiler errors.
- Inspect scenes, hierarchy, prefabs, assets, scripts, packages, and project settings.
- Execute approved Editor operations.
- Provide snapshots, undo support, and rollback checkpoints where possible.
- Validate Meta XR configuration and Quest readiness.

### Capability system

Capabilities are the extension boundary. The core should stay small; new behavior should be added through capabilities.

```yaml
name: meta_xr.validate_setup
description: Validate Meta XR project setup for Quest.
permissions:
  - read_project_settings
  - read_packages
  - read_scenes
effects:
  - report_only
verification:
  - console_clean
  - xr_settings_valid
```

### Agent ecosystem

Agents should consume capabilities instead of directly manipulating project files.

Initial agents:

- Scene Architect — creates and adjusts scenes, layout, lighting, and spatial structure.
- Meta XR Specialist — configures rigs, hands, passthrough, anchors, OpenXR, and Quest settings.
- Log Resolver — reads console output, identifies root causes, applies fixes, and verifies compilation.
- Vision QA — uses screenshots to detect visual regressions and compare before/after states.
- Performance Auditor — checks Quest performance risks, shaders, lighting, draw calls, assets, and build settings.
- Build Engineer — validates Android/Quest build readiness and produces release reports.

## Safety model

The project should support broad control over Unity, but never uncontrolled execution by default.

Required safety layers:

- Explicit permission model per capability.
- Dry-run/preview for high-impact actions.
- Confirmation before destructive operations.
- Audit trail for every agent action.
- Snapshots or rollback checkpoints before risky changes.
- Verification before reporting success.

## Extensibility goals

- Provider-neutral agents: compatible with any MCP-capable AI host.
- Public capability SDK for community extensions.
- Adapter-first design for Unity packages beyond Meta XR.
- Example projects that demonstrate real workflows, not toy prompts.
- Clear contribution path for tools, agents, validators, and adapters.

## First milestone

Build the smallest closed-loop prototype:

1. Unity plugin connects to the MCP server.
2. MCP server can inspect the active project and read Unity Console logs.
3. Plugin captures Scene View or Game View screenshots.
4. A log resolver agent can diagnose a compiler/runtime error.
5. The system applies a controlled fix.
6. Unity recompiles.
7. The system verifies logs and captures a final screenshot/report.

Success criteria:

- The agent can observe Unity state through structured data and screenshots.
- The agent can apply at least one controlled Unity-side change.
- The agent can verify that the project improved after the change.
- All actions are auditable and safe enough for real project usage.
