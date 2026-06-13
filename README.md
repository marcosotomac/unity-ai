# Unity AI Control Plane

[![CI](https://github.com/marcosotomac/unity-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/marcosotomac/unity-ai/actions/workflows/ci.yml)

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

Exposes project-grade tools to AI agents while defining capability contracts and routing requests. Runtime safety includes local token gating for mutating routes, dry-run and confirmation gates, persistent jobs, hashed durable checkpoints, persisted audit events for established edit flows, post-action verification, isolated Unity Undo groups, and atomic rollback for declarative scene batches. Fine-grained runtime permission policy and uniform audit coverage across every newer operation remain hardening work.

Initial tool families:

- `unity.project.*` — inspect project structure, packages, settings, scenes, scripts, and assets.
- `unity.assets.*` — list project assets with GUIDs, paths, and main asset types.
- `unity.asset.*` — inspect specific asset metadata and dependencies.
- `unity.prefabs.*` / `unity.prefab.*` — list and inspect prefab assets.
- `unity.scene.*` — inspect hierarchies and serialized component state, then create, duplicate, rename, reparent, delete, instantiate prefabs, add/remove components, and set serialized properties through atomic batches.
- `unity.scenes.*` — list scenes discovered in the project and Build Settings.
- `unity.scripts.*` / `unity.assemblies.*` — inspect C# scripts and Unity script assemblies.
- `unity.packages.*` — list and change registry packages through reload-safe jobs.
- `unity.jobs.*` — inspect and cancel persistent long-running operations.
- `unity.playmode.*` / `unity.compilation.*` — control Play Mode and wait for compilation/import plus console verification.
- `unity.checkpoints.*` — create, list, restore, verify, and delete durable project checkpoints.
- `unity.vision.*` — synchronously capture verified Scene View/Game View PNGs, compare before/after artifacts, generate diff images, and detect threshold-based regressions.
- `unity.console.*` — read logs, group errors by likely root cause, and verify fixes.
- `unity.meta_xr.*` — validate and configure Meta XR SDK, OpenXR, rigs, hands, passthrough, anchors, interactions, and Quest build requirements.
- `unity.tests.*` — run Edit Mode/Play Mode tests and summarize failures.
- `unity.build.*` — validate and execute builds, especially Android/Quest targets.
- `unity.assets.author` — create or edit shaders, materials, animation clips, generated WAV audio, and audio import settings.
- `unity.prefab.manage` — save prefab assets, create variants, edit prefab contents, and apply/revert overrides.

### Unity Editor plugin

Runs inside Unity and provides the actual control plane over the project.

Responsibilities:

- Capture Scene View and Game View screenshots.
- Read Unity Console logs and compiler errors.
- Inspect scenes, hierarchy, prefabs, assets, scripts, packages, and project settings.
- Execute approved Editor operations.
- Provide snapshots, undo support, and rollback checkpoints where possible.
- Validate Meta XR configuration and Quest readiness.
- Persist long-running tests, builds, package changes, Play Mode transitions, compilation waits, and Meta XR setup across domain reloads.

See `docs/control-plane-operations.md` for operational contracts and examples.

### Capability system

Capabilities are the extension boundary. The core should stay small; new behavior should be added through capabilities.

The broad scene-authoring boundary is `unity.scene.batch`. It provides reusable Unity operations without exposing arbitrary C# execution or reflective method invocation. See `docs/scene-authoring.md`.

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
4. A log resolver agent can diagnose a compiler/runtime error and propose a safe read-only fix plan.
5. The system applies a controlled fix after confirmation.
6. Unity recompiles.
7. The system verifies logs and captures a final screenshot/report.

Success criteria:

- The agent can observe Unity state through structured data and screenshots.
- The agent can apply at least one controlled Unity-side change.
- The agent can verify that the project improved after the change.
- All actions are auditable and safe enough for real project usage.

## Quickstart: automatic setup

Run the setup script from this repository to install the Unity package into a local Unity project and/or configure opencode, Claude Code, or Codex to start the MCP server.

Dry run first:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode --claude-code --codex
```

Apply the changes:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode --claude-code --codex --build --write
```

What it changes:

- Adds or updates `Packages/manifest.json` in the target Unity project with a local `file:` dependency to this repo's Unity package.
- Adds or updates `mcp.unity-ai` in `~/.config/opencode/opencode.json` using an absolute path to `apps/mcp-server/dist/index.js`.
- Configures Claude Code through `claude mcp add-json unity-ai ...`; the script does not edit Claude Code config files directly.
- Adds or updates a generated `unity-ai` MCP server block in `~/.codex/config.toml`.
- Creates `.bak-YYYYMMDDHHmmss` backups next to files before writing.
- Generates and prints a local bridge token when applying with `--write` and an MCP host without `--bridge-token`; use that same token when starting the Unity local bridge.

After changing opencode or Codex config, restart the host so it reloads config. Claude Code is configured through its CLI.

See `docs/setup.md` for focused setup details and examples.

## Development

Requirements:

- Node.js 20 or newer.
- npm.
- Unity Editor 2022.3 or newer for Unity package verification.

Useful commands:

```bash
npm ci
npm run typecheck
npm run build
npm run verify:unity-package
```

`npm run verify:unity-package` expects a local Unity installation. Set `UNITY_PATH` or pass the Unity executable path as the first argument if Unity is not discoverable in the default location.
