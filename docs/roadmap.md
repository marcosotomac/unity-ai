# Roadmap

## Milestone 1 — Closed-loop scaffold

- [x] Product README with observe-act-verify thesis.
- [x] TypeScript workspace for MCP and protocol packages.
- [x] Unity package scaffold for the Editor plugin.
- [x] Initial capability contract.
- [x] Initial console, project, screenshot, and Meta XR validation stubs.

## Milestone 2 — Local bridge

- [x] Verify Unity package compilation in Unity 6000.4.9f1.
- [x] Add reproducible Unity package verification script.
- [x] Choose and implement local transport between MCP server and Unity plugin.
- [x] Route `unity.project.inspect` from MCP to Unity.
- [x] Route `unity.console.read` from MCP to Unity.
- [x] Route `unity.vision.capture` from MCP to Unity.
- [x] Return structured unavailable states for missing Scene View contexts.
- [ ] Add screenshot artifact readiness checks before reporting capture success.
- [x] Test MCP tool calls against a running Unity Editor instance.

Verified tools:

- `unity.capabilities.list`
- `unity.project.inspect`
- `unity.console.read`
- `unity.meta_xr.validate_setup`
- `unity.vision.capture`
- `unity.editor.create_empty_game_object`
- `unity.editor.undo_last_operation`

## Milestone 3 — First diagnose/fix loop

- [x] Add first controlled act capability with dry-run and audit output.
- [x] Semantically verify dry-run and real creation in MCP ↔ Unity e2e.
- [x] Add local bridge token/auth before exposing additional mutating operations.
- [x] Emit structured audit events and verification signals for the first act capability.
- [x] Add machine-readable `scene_mutation_verified` signal for real scene mutation.
- [x] Persist audit events beyond the immediate tool response.
- [x] Add confirmation gate for the first scene mutation.
- [x] Add rollback/undo verification for the first scene mutation.
- [x] Add request/correlation IDs to act and rollback audit events.
- [ ] Detect a simple Unity compiler or runtime error.
- [ ] Propose a safe fix plan.
- [ ] Apply one controlled Editor-side change.
- [ ] Recompile and verify console state.
- [ ] Produce an audit report with before/after evidence.

## Milestone 3.5 — Real project observability

- [x] Add `unity.assets.list`.
- [x] Add `unity.scenes.list`.
- [x] Add `unity.scene.inspect`.
- [x] Add `unity.packages.list`.
- [x] Add `unity.project.settings.inspect`.
- [x] Add `unity.asset.dependencies`.
- [x] Add `unity.prefabs.list`.
- [x] Add `unity.prefab.inspect`.
- [x] Add `unity.scripts.list`.
- [x] Add `unity.assemblies.list`.
- [ ] Add material/shader/audio/texture-specific inspection.
- [ ] Add prefab variant/override inspection.

## Milestone 4 — Meta XR readiness

- [ ] Validate Android build target, OpenXR, Meta XR packages, rig setup, permissions, and Quest-specific settings.
- [ ] Add actionable fix plans for common Meta XR project issues.
- [ ] Add screenshot-based visual verification for XR scene setup.
