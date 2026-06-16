# Safety Model

The product should be powerful enough to operate real Unity projects, but controlled enough to be trustworthy.

## Safety layers

| Layer | Requirement |
|-------|-------------|
| Permissions | Every capability declares what it needs before execution. |
| Preview | High-impact operations expose a plan before changing the project. |
| Confirmation | Destructive or broad changes require explicit approval. |
| Audit | Every action records what happened, when, and through which capability. |
| Rollback | Risky operations should create snapshots or use Unity undo where possible. |
| Verification | The system must re-observe the project before claiming success. |

## Default stance

Read-only and report-only operations are safe by default. Mutations, generated scripts, package changes, build changes, and destructive actions require stronger gates.

## Generated runtime code

`unity.scripts.author` does not expose unrestricted Editor scripting. It only accepts project `MonoBehaviour` source after a dry-run validation and exact SHA-256 confirmation. The operation blocks Editor APIs, edit-time callbacks, process, file, network, reflection, native interop, unsafe code, and termination APIs; checkpoints the target; recompiles through a persistent job; verifies the resolved class; and rolls back by default when compilation or attachment fails.

These controls are defense in depth, not a C# sandbox. A source-pattern policy cannot prove arbitrary gameplay code harmless, so generated components still require normal code review, tests, and project-level trust.
