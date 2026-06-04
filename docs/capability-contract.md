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
| `unity.scene.*` | Inspect active scene hierarchy and scene state. |
| `unity.scripts.*` | Inspect C# scripts visible to Unity. |
| `unity.assemblies.*` | Inspect Unity script assemblies. |
| `unity.console.*` | Read, summarize, and verify logs. |
| `unity.vision.*` | Capture screenshots and support visual checks. |
| `unity.editor.*` | Execute approved Editor operations. Only `unity.editor.create_empty_game_object` and `unity.editor.undo_last_operation` are exposed today; broader operations remain planned. |
| `unity.meta_xr.*` | Validate and configure Meta XR project setup. |
