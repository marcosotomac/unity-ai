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
- prefab assets, variants, edits, and override management;
- Project Settings, Build Settings, package changes, and durable hashed checkpoints;
- controlled command routing for approved Editor operations.

Broad authoring remains declarative: the plugin does not execute arbitrary generated C# or invoke arbitrary methods through reflection.
