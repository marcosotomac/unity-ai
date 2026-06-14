# Unity Editor Plugin

This folder contains the Unity package scaffold for the local control plane plugin.

Import path during development:

```text
apps/unity-plugin/Packages/com.unity-ai.control-plane
```

The plugin starts with editor-only capabilities for:

- project inspection;
- console log summaries;
- Scene View and Game View screenshots;
- verified screenshot readiness, before/after comparison, diff artifacts, and visual regression decisions;
- Meta XR/OpenXR package, loader, feature, and Android/Quest validation plus automatic configuration;
- detailed GameObject/component/serialized-property inspection;
- atomic scene batches for hierarchy, prefab, component, and serialized-property authoring;
- persistent Edit Mode/Play Mode test jobs and Play Mode control;
- compilation/import settling with console verification;
- Android/Quest build validation and APK/AAB jobs;
- shader, material, animation, WAV, and audio-import authoring;
- local/HTTPS model, texture, and audio import with size/hash verification and importer configuration;
- Animator Controller authoring plus reusable rotation, bobbing, and pulse runtime behaviours;
- hash-confirmed runtime `MonoBehaviour` generation with blocked high-risk APIs, persistent compilation checks, optional attachment, and checkpoint rollback;
- high-level proximity door, pickup, and multi-target activator composition with direct scene-reference wiring;
- prefab assets, variants, edits, and override management;
- Project Settings, Build Settings, package changes, and durable hashed checkpoints;
- correlated JSON/Markdown audit reports with hashed before/after evidence;
- controlled command routing for approved Editor operations.

Broad authoring remains declarative. Custom runtime components pass a conservative source policy and exact-hash confirmation before compilation; arbitrary Editor code and reflective method invocation are not exposed.
