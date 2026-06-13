# Capability Contract

Capabilities are the public extension point for agents, Unity operations, validators, and package-specific adapters.

## Required fields

```yaml
name: unity.console.read
description: Read Unity Console messages and summarize issues.
permissions:
  - read_console
effects:
  - report_only
verification:
  - console_snapshot
```

```yaml
name: unity.console.diagnose
description: Classify Unity Console entries and return safe diagnostic guidance.
permissions:
  - read_console
effects:
  - report_only
verification:
  - console_diagnostics
```

```yaml
name: unity.console.plan_fix
description: Derive conservative read-only fix plans from Unity Console diagnostics.
permissions:
  - read_console
  - read_project
effects:
  - report_only
verification:
  - console_diagnostics
  - fix_plan_generated
```

## Contract rules

- A capability must declare its permissions before execution.
- A capability must declare expected side effects.
- Risky capabilities must support dry-run or confirmation.
- Mutating capabilities must emit audit events.
- Any capability that acts should also define how it verifies the result.

## First capability families

| Family | Purpose |
|--------|---------|
| `unity.project.*` | Inspect Unity project structure and settings. |
| `unity.assets.*` | Inspect project assets and asset metadata. |
| `unity.asset.*` | Inspect a specific asset, including dependencies. |
| `unity.prefabs.*` | List prefab assets. |
| `unity.prefab.*` | Inspect a specific prefab asset. |
| `unity.scenes.*` | List Unity scenes. |
| `unity.scene.*` | Inspect hierarchy/component state and apply controlled, atomic scene authoring batches. |
| `unity.scripts.*` | Inspect C# scripts visible to Unity. |
| `unity.assemblies.*` | Inspect Unity script assemblies. |
| `unity.console.*` | Read, summarize, and verify logs. |
| `unity.vision.*` | Capture ready screenshots, compare before/after artifacts, generate diffs, and detect visual regressions. |
| `unity.jobs.*` | Inspect and cancel persistent long-running operations. |
| `unity.tests.*` | Run Edit Mode and Play Mode tests with XML evidence. |
| `unity.playmode.*` | Inspect and control Play Mode. |
| `unity.compilation.*` | Wait for compilation/import and verify console state. |
| `unity.build.*` | Validate and produce Android/Quest builds. |
| `unity.checkpoints.*` | Create and restore durable hashed project snapshots. |
| `unity.editor.*` | Execute approved Editor operations and explicit rollback commands. |
| `unity.meta_xr.*` | Validate and configure Meta XR project setup. |

## Broad scene authoring

`unity.scene.inspect_game_object` returns bounded component and visible serialized-property metadata for one hierarchy path.

`unity.scene.batch` accepts up to 50 declarative operations and commits them as one isolated Unity Undo group. Supported operations include hierarchy creation/deletion, duplication, rename, reparenting, active state, prefab instantiation, component add/remove, and serialized-property writes.

The batch capability does not invoke arbitrary methods or execute generated C#.
