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

Read-only and report-only operations are safe by default. Mutations, generated Editor scripts, package changes, build changes, and destructive actions require stronger gates.
