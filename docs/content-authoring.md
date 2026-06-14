# Content Authoring

The control plane supports a closed content workflow:

```text
acquire → import → configure → instantiate → animate → add behaviour → verify
```

## Import a model

`unity.assets.import` accepts a local absolute file path or a remote URL and always writes under `Assets/`.

```json
{
  "dryRun": false,
  "confirm": true,
  "sourceKind": "url",
  "url": "https://example.com/robot.fbx",
  "destinationPath": "Assets/Imported/Robot/robot.fbx",
  "expectedSha256": "64-character-sha256",
  "model": {
    "globalScale": 1,
    "animationType": "generic",
    "importAnimation": true,
    "addCollider": false
  },
  "instantiate": true,
  "objectName": "Robot",
  "saveAsPrefabPath": "Assets/Imported/Robot/Robot.prefab"
}
```

Remote production imports require HTTPS. Every transfer is bounded by `maxBytes` and `timeoutSeconds`; private-network destinations are rejected. Supplying `expectedSha256` is recommended.

FBX, OBJ, DAE, 3DS, and DXF use Unity's built-in model importer. GLB/glTF paths are accepted when the project has a compatible glTF importer package installed.

## Create animation logic

Use `unity.assets.author` with `kind: "animation_clip"` to write curves and `kind: "animator_controller"` to assemble states, parameters, transitions, and conditions.

Assign the generated controller with `unity.scene.batch`:

```json
{
  "dryRun": false,
  "confirm": true,
  "operations": [
    {
      "kind": "add_component",
      "targetPath": "Robot",
      "componentType": "UnityEngine.Animator"
    },
    {
      "kind": "set_property",
      "targetPath": "Robot",
      "componentType": "UnityEngine.Animator",
      "propertyPath": "m_Controller",
      "value": {
        "kind": "object_reference",
        "assetPath": "Assets/Imported/Robot/Robot.controller"
      }
    }
  ]
}
```

## Add functionality

`unity.scene.batch` can add any unambiguous compiled `Component` already present in Unity, the project, or an installed package, then configure its serialized fields.

The control-plane runtime package includes:

- `UnityAI.ControlPlane.Runtime.ContinuousRotation`
- `UnityAI.ControlPlane.Runtime.BobbingMotion`
- `UnityAI.ControlPlane.Runtime.PulseScale`

This keeps common authoring declarative.

## Compose gameplay templates

`unity.gameplay.compose` converts existing scene objects into complete, reusable gameplay mechanics without generating a unique script for every object. One confirmed request can atomically configure:

- a proximity door with opening offset, speed, close distance, and explicit interactor;
- a pickup with value, identity, collection action, spin, and bob motion;
- a proximity activator that activates, deactivates, or toggles up to 32 target objects.

```json
{
  "dryRun": false,
  "confirm": true,
  "templates": [
    {
      "kind": "door",
      "targetPath": "Environment/MainDoor",
      "interactorPath": "Player",
      "activationDistance": 2.5,
      "deactivationDistance": 3.5,
      "openOffset": { "x": 0, "y": 3, "z": 0 },
      "speed": 4
    },
    {
      "kind": "pickup",
      "targetPath": "Items/Crystal",
      "interactorPath": "Player",
      "pickupId": "crystal-blue",
      "value": 25,
      "collectAction": "deactivate"
    },
    {
      "kind": "activator",
      "targetPath": "Triggers/SecretRoom",
      "interactorPath": "Player",
      "affectedPaths": ["Environment/HiddenBridge", "UI/SecretFound"],
      "action": "activate",
      "oneShot": true
    }
  ]
}
```

The operation requires a saved scene, creates a durable scene checkpoint, applies all templates in one Undo group, verifies every component and reference, and rolls the in-memory transaction back if any template fails. An explicit `interactorPath` is preferred; `interactorTag` is available as a reusable fallback.

## Generate custom runtime behaviour

`unity.scripts.author` creates a project `MonoBehaviour` through a gated workflow:

1. Submit the exact source in dry-run mode.
2. Review the validation result and returned SHA-256.
3. Resubmit with `dryRun: false`, `confirm: true`, and that exact `expectedSourceSha256`.
4. Unity writes the source behind a durable checkpoint, recompiles, resolves the concrete class, and optionally attaches it to a GameObject.
5. Compilation or attachment failure restores the checkpoint by default.

Generated code is limited to runtime components. The validator rejects Editor APIs, edit-time callbacks, file and network access, process execution, reflection, native interop, unsafe code, static constructors, and application termination. The filename, class, namespace, and `MonoBehaviour` inheritance are verified again after compilation.

```json
{
  "dryRun": false,
  "confirm": true,
  "path": "Assets/Generated/DoorTrigger.cs",
  "source": "using UnityEngine;\nnamespace Game.Generated { public sealed class DoorTrigger : MonoBehaviour { public float speed = 2f; private void Update() { transform.Translate(Vector3.up * speed * Time.deltaTime); } } }\n",
  "expectedSourceSha256": "sha256-returned-by-the-dry-run",
  "expectedClassName": "DoorTrigger",
  "expectedNamespace": "Game.Generated",
  "attachToObjectPath": "Environment/Door"
}
```

This source policy is a risk gate, not a formal security sandbox. Generated gameplay code should still be reviewed and tested like any other project code.
