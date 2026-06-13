# Unity Package Verification

The TypeScript workspace can be checked without Unity, but the Editor plugin must be compiled inside Unity.

## Run the verification

Install Unity and run:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/2022.3.x/Unity.app/Contents/MacOS/Unity" npm run verify:unity-package
```

Verified locally with:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:unity-package
```

The script creates a temporary Unity project, imports the local package, launches Unity in batchmode, and writes the Editor log to:

```text
artifacts/unity-verification/editor-compile.log
```

## What this verifies

- Unity package manifest can be imported.
- Editor assembly definitions are valid.
- C# Editor scripts compile against Unity APIs.

## What this does not verify yet

- Meta XR configuration against a production project or Quest hardware.
- A real APK/AAB when Android Build Support is absent from the verification Editor.
- Graphically meaningful camera output under `-nographics`; headless CI verifies PNG readiness and comparison logic independently.

## Next validation after compile passes

1. Open Unity.
2. Open `Tools → Unity AI → Control Plane`.
3. Click `Start Local Bridge`.
4. Invoke MCP tools from a client:
   - `unity.project.inspect`
   - `unity.console.read`
   - `unity.console.diagnose`
   - `unity.console.plan_fix`
   - `unity.vision.capture`
   - `unity.meta_xr.validate_setup`

## End-to-end MCP bridge verification

Run:

```bash
UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:mcp-unity-e2e
```

This uses a cached verification project at:

```text
.unity-ai/e2e-project
```

The first run can be slow because Unity creates `Library/` and imports packages. Later runs reuse the cache.

Verified tools:

- `unity.capabilities.list`
- `unity.project.inspect`
- `unity.console.read`
- `unity.console.diagnose`
- `unity.console.plan_fix`
- `unity.assets.list`
- `unity.scenes.list`
- `unity.scene.inspect`
- `unity.scene.inspect_game_object`
- `unity.scene.upsert_game_object`
- `unity.scene.batch`
- `unity.prefabs.list`
- `unity.prefab.inspect`
- `unity.asset.dependencies`
- `unity.scripts.list`
- `unity.assemblies.list`
- `unity.packages.list`
- `unity.project.settings.inspect`
- `unity.meta_xr.validate_setup`
- `unity.vision.capture`
- `unity.vision.compare`
- `unity.editor.create_empty_game_object`
- `unity.editor.undo_last_operation`
- `unity.jobs.get`
- `unity.compilation.wait`
- `unity.tests.run` for Edit Mode and Play Mode
- `unity.playmode.status` and `unity.playmode.control`
- `unity.build.validate_android_quest` and build dry-run
- `unity.assets.author`
- `unity.prefab.manage`
- `unity.checkpoints.create`, `list`, `restore`, and `delete`
- `unity.project.settings.update` dry-run
- `unity.packages.change` dry-run
- `unity.meta_xr.configure` dry-run

For a clean CI-style run:

```bash
UNITY_AI_E2E_CLEAN=1 UNITY_PATH="/Applications/Unity/Hub/Editor/6000.4.9f1/Unity.app/Contents/MacOS/Unity" npm run verify:mcp-unity-e2e
```
