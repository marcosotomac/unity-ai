# Visual Verification

Visual verification uses ready PNG artifacts stored under:

```text
UnityAIArtifacts/Screenshots
```

## Capture

`unity.vision.capture` synchronously renders a Scene View or game camera. It returns `ready: true` only after the file:

- exists and is non-empty;
- decodes as an image;
- has valid dimensions;
- has a computed SHA-256 hash.

For projects with multiple cameras, pass `cameraPath` to select one deterministically.

```json
{
  "source": "game",
  "cameraPath": "XR Origin/Head/Main Camera",
  "width": 1280,
  "height": 720,
  "label": "before"
}
```

The returned `path` is project-relative and can be passed directly to comparison.

## Compare

Capture before and after the operation, then call `unity.vision.compare`:

```json
{
  "beforePath": "UnityAIArtifacts/Screenshots/game-view-before.png",
  "afterPath": "UnityAIArtifacts/Screenshots/game-view-after.png",
  "pixelThreshold": 0.1,
  "maxChangedPixelRatio": 0.01,
  "maxMeanAbsoluteError": 0.02,
  "ignoreAlpha": true,
  "generateDiff": true,
  "label": "lighting-change"
}
```

The result includes:

- changed pixel count and ratio;
- mean absolute error;
- root mean square error;
- maximum channel error;
- regression reasons;
- `regressionDetected`;
- a ready diff artifact when dimensions match and `generateDiff` is enabled.

A pixel is considered changed when its maximum normalized channel difference exceeds `pixelThreshold`. A regression is reported when the changed-pixel ratio or mean absolute error exceeds its configured maximum. Different image dimensions are always a regression.

## Recommended Workflow

1. Capture a stable baseline from an explicit camera and fixed resolution.
2. Apply the Unity operation.
3. Capture the candidate with the same camera and resolution.
4. Compare using thresholds appropriate for the scene.
5. Inspect the generated diff and regression reasons.

Use strict thresholds for deterministic UI and looser thresholds for antialiasing, particles, temporal effects, shadows, or platform-dependent rendering.

Unity `-nographics` can produce a placeholder frame instead of meaningful scene output. Use a graphical Editor or player run for production visual baselines.
