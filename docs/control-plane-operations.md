# Control Plane Operations

## Persistent jobs

Long-running operations return a `jobId`. Poll `unity.jobs.get`, filter with `unity.jobs.list`, or request cancellation with `unity.jobs.cancel`.

Job records survive assembly/domain reloads under:

```text
Library/UnityAIControlPlane/Jobs
```

Terminal states are `succeeded`, `failed`, and `cancelled`. Successful jobs include JSON evidence in `resultJson` and machine-readable `verificationSignals`.

## Tests and compilation

- `unity.tests.run`: Edit Mode or Play Mode filters, confirmation, persistent state, XML results under `UnityAIArtifacts/TestResults`.
- `unity.compilation.wait`: optional asset refresh, timeout, stable-frame settling, and maximum accepted console error count.
- `unity.playmode.control`: `enter`, `exit`, `pause`, `resume`, or `step`.

## Android and Quest

`unity.build.validate_android_quest` checks Android Build Support, active target, enabled scenes, application identifier, IL2CPP, ARM64, SDK settings, XR Management, OpenXR, and Meta OpenXR.

`unity.build.android` writes APK/AAB outputs only under:

```text
UnityAIArtifacts/Builds
```

`unity.meta_xr.configure` can install the XR packages, switch to Android, apply IL2CPP/ARM64/API 29+, assign `OpenXRLoader`, enable available Meta Quest and controller features, and run final validation.

## Assets and prefabs

`unity.assets.author` supports:

- `.shader` source;
- `.mat` shader/properties/keywords/render queue;
- `.anim` curve bindings and keyframes;
- `.controller` Animator parameters, states, clips, transitions, and conditions;
- generated PCM16 `.wav` tones;
- audio importer settings.

`unity.assets.import` supports:

- local absolute source files with the explicit `read_external_files` permission;
- HTTPS downloads with redirect, private-address, timeout, byte-limit, and optional SHA-256 checks;
- explicit loopback HTTP only for local testing;
- FBX, OBJ, DAE, 3DS, and DXF model import, plus GLB/glTF when a compatible importer package is installed;
- common texture and audio formats;
- `ModelImporter`, `TextureImporter`, and `AudioImporter` settings;
- optional scene instantiation and prefab creation.

Imported objects can be composed through `unity.scene.batch` with built-in, project, or package components. The package includes `ContinuousRotation`, `BobbingMotion`, and `PulseScale` runtime behaviours.

`unity.scripts.author` extends composition to custom runtime behaviours. It requires a validated dry-run SHA-256, creates a durable checkpoint, blocks high-risk API families and edit-time execution, waits across Unity domain reloads, verifies the compiled `MonoBehaviour`, optionally attaches it, and restores the checkpoint on failure.

`unity.gameplay.compose` operates one level above component editing. It configures reusable proximity doors, pickups, and activators, resolves direct scene references, checkpoints the saved scene, applies all requested templates atomically, and verifies the resulting component state.

`unity.prefab.manage` supports:

- save a scene object as a prefab;
- create a prefab variant;
- edit prefab contents with bounded hierarchy/component/property operations;
- apply or revert instance overrides.

## Durable checkpoints

`unity.checkpoints.create` copies selected project-relative files, records absent paths, and stores SHA-256 metadata under:

```text
UnityAIArtifacts/Checkpoints
```

`unity.checkpoints.restore` optionally creates a safety checkpoint, restores files, removes paths that were originally absent, refreshes Unity, and verifies hashes. This provides rollback beyond the lifetime and scope of Unity Undo.

All mutating operations require the local bridge token. High-impact operations also default to `dryRun: true` or require `confirm: true`.

## Audit reports

`unity.audit.report` filters persisted JSONL events by request ID, correlation ID, capability, or UTC interval. It accepts project-relative evidence files only under `UnityAIArtifacts`, verifies each file with SHA-256, validates verification-to-evidence references, and can require complete `before` and `after` phases.

Each call writes a machine-readable JSON report and a Markdown report under:

```text
UnityAIArtifacts/Audit/Reports
```

A report is `passed` only when supplied evidence exists, verification claims pass, requested audit filters match, and any required before/after pair is complete.
