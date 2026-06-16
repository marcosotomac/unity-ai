# Scene Authoring

The control plane exposes broad Unity scene control through two complementary MCP tools.

## Inspect a filtered hierarchy

`unity.scene.inspect` can combine filters while keeping the response bounded:

```json
{
  "includeComponents": true,
  "maxDepth": 10,
  "maxGameObjects": 50,
  "filter": {
    "componentType": "UnityEngine.Light",
    "withinRadius": {
      "centerPath": "Player",
      "radius": 5
    }
  }
}
```

The report distinguishes visited, matched, and returned objects and includes world position and distance from the radius center.

## Inspect one GameObject

`unity.scene.inspect_game_object` reads:

- hierarchy identity, active state, tag, layer, and local transform;
- component type, assembly, script asset path, and stable type index;
- bounded visible `SerializedProperty` paths, types, editability, values, and asset/scene references.

Use the returned property path and component type as inputs to a later batch.

## Apply an atomic batch

`unity.scene.batch` accepts 1 to 50 operations:

- `create`
- `instantiate_prefab`
- `delete`
- `duplicate`
- `rename`
- `reparent`
- `set_active`
- `add_component`
- `remove_component`
- `set_property`

Every real batch requires the bridge token, `dryRun: false`, and `confirm: true`. Unity executes the operations in one isolated Undo group. If any operation fails or its result cannot be verified, the complete group is reverted.

```json
{
  "dryRun": false,
  "confirm": true,
  "operations": [
    {
      "kind": "create",
      "name": "Player",
      "primitive": "capsule"
    },
    {
      "kind": "add_component",
      "targetPath": "Player",
      "componentType": "UnityEngine.Rigidbody"
    },
    {
      "kind": "set_property",
      "targetPath": "Player",
      "componentType": "UnityEngine.Rigidbody",
      "componentIndex": 0,
      "propertyPath": "m_Mass",
      "value": {
        "kind": "number",
        "numberValue": 2.5
      }
    }
  ]
}
```

Supported serialized value kinds are `bool`, `integer`, `number`, `string`, `color`, vectors, integer vectors, rects, bounds, quaternion, enum, asset object reference, null, array size, and character.

Scene references use `game_object_reference` with a hierarchy `path`, or `component_reference` with `path`, `componentType`, and `componentIndex`:

```json
{
  "kind": "set_property",
  "targetPath": "Door",
  "componentType": "Game.DoorController",
  "propertyPath": "player",
  "value": {
    "kind": "component_reference",
    "path": "Player",
    "componentType": "Game.PlayerController",
    "componentIndex": 0
  }
}
```

Asset references remain restricted to existing `Assets/` or `Packages/` paths. Component types must resolve to concrete Unity `Component` types. `Transform` cannot be added or removed, and `m_Script` cannot be replaced.

The batch API does not execute arbitrary C# or invoke arbitrary methods. Package-specific behavior should be added as reviewed capabilities or adapters on top of this authoring layer.
