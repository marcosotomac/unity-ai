import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

import { initialCapabilities } from "./capabilities.js";
import { UnityBridgeClient } from "./unity-bridge-client.js";

const bridge = new UnityBridgeClient({
  baseUrl: process.env.UNITY_AI_BRIDGE_URL ?? "http://127.0.0.1:39071",
  timeoutMs: parseTimeout(process.env.UNITY_AI_BRIDGE_TIMEOUT_MS),
  token: process.env.UNITY_AI_BRIDGE_TOKEN
});

const server = new McpServer({
  name: "unity-ai-control-plane",
  version: "0.1.0"
});

server.registerTool(
  "unity.capabilities.list",
  {
    description: "List the Unity AI Control Plane capabilities known by the MCP server.",
    inputSchema: z.object({})
  },
  async () => ({
    content: [
      {
        type: "text",
        text: JSON.stringify(initialCapabilities, null, 2)
      }
    ]
  })
);

server.registerTool(
  "unity.project.inspect",
  {
    description: "Inspect the active Unity project through the local Unity Editor bridge.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.project.inspect")
);

server.registerTool(
  "unity.project.snapshot",
  {
    description: "Capture a compact, sanitized, read-only Unity project context snapshot for planning next actions.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.project.snapshot")
);

server.registerTool(
  "unity.console.read",
  {
    description: "Read Unity Console summary and recent log entries through the local Unity Editor bridge.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.console.read")
);

server.registerTool(
  "unity.console.diagnose",
  {
    description: "Diagnose Unity Console compiler/runtime issues as structured, read-only guidance.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.console.diagnose")
);

server.registerTool(
  "unity.console.plan_fix",
  {
    description: "Generate conservative read-only fix plans from Unity Console diagnostics.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.console.plan_fix")
);

server.registerTool(
  "unity.console.apply_fix",
  {
    description: "Apply a confirmed, checkpointed one-line replacement to a Unity C# script.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      targetFile: z.string().min(1),
      targetLine: z.number().int().min(1),
      expectedOriginalLine: z.string(),
      replacementLine: z.string(),
      expectedDiagnosticCategory: z.string().optional(),
      expectedMessageContains: z.string().optional(),
      planId: z.string().optional()
    })
  },
  async (input) => bridgeTool("unity.console.apply_fix", input)
);

server.registerTool(
  "unity.assets.list",
  {
    description: "List Unity project assets with paths, GUIDs, and main asset types.",
    inputSchema: z.object({
      folder: z.string().default("Assets"),
      maxResults: z.number().int().min(1).max(1000).default(200)
    })
  },
  async ({ folder, maxResults }) => bridgeTool("unity.assets.list", { folder, maxResults })
);

server.registerTool(
  "unity.scenes.list",
  {
    description: "List Unity scenes in the project and Build Settings.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.scenes.list")
);

server.registerTool(
  "unity.scene.inspect",
  {
    description: "Inspect the active Unity scene hierarchy at a high level.",
    inputSchema: z.object({
      includeComponents: z.boolean().default(true),
      maxDepth: z.number().int().min(0).max(10).default(3),
      maxGameObjects: z.number().int().min(1).max(1000).default(200)
    })
  },
  async ({ includeComponents, maxDepth, maxGameObjects }) => bridgeTool("unity.scene.inspect", { includeComponents, maxDepth, maxGameObjects })
);

server.registerTool(
  "unity.scene.inspect_game_object",
  {
    description: "Inspect one GameObject, its components, and bounded visible serialized properties.",
    inputSchema: z.object({
      path: z.string().min(1).max(512),
      includeProperties: z.boolean().default(true),
      maxProperties: z.number().int().min(1).max(2000).default(500),
      maxPropertyDepth: z.number().int().min(0).max(20).default(8)
    }).strict()
  },
  async (input) => bridgeTool("unity.scene.inspect_game_object", input)
);

const sceneVectorSchema = z.object({
  x: z.number().finite(),
  y: z.number().finite(),
  z: z.number().finite()
}).strict();

server.registerTool(
  "unity.scene.upsert_game_object",
  {
    description: "Create or update a GameObject in the active Unity scene from a safe, schema-bound spec.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      name: z.string().min(1).max(80),
      path: z.string().min(1).max(512).optional(),
      primitive: z.enum(["empty", "cube", "sphere", "capsule", "cylinder", "plane", "quad"]).default("empty"),
      transform: z.object({
        position: sceneVectorSchema.optional(),
        rotationEuler: sceneVectorSchema.optional(),
        scale: sceneVectorSchema.optional()
      }).strict().optional(),
      tag: z.string().min(1).max(80).optional(),
      layer: z.union([z.string().min(1).max(80), z.number().int().min(0).max(31)]).optional(),
      active: z.boolean().optional(),
      mode: z.enum(["create", "update", "upsert"]).default("upsert")
    }).strict()
  },
  async (input) => bridgeTool("unity.scene.upsert_game_object", input)
);

const sceneSerializedValueSchema = z.union([
  z.object({ kind: z.literal("bool"), boolValue: z.boolean() }).strict(),
  z.object({ kind: z.literal("integer"), integerValue: z.number().int().safe() }).strict(),
  z.object({ kind: z.literal("number"), numberValue: z.number().finite() }).strict(),
  z.object({ kind: z.literal("string"), stringValue: z.string() }).strict(),
  z.object({ kind: z.literal("color"), x: z.number().finite(), y: z.number().finite(), z: z.number().finite(), w: z.number().finite() }).strict(),
  z.object({ kind: z.literal("vector2"), x: z.number().finite(), y: z.number().finite() }).strict(),
  z.object({ kind: z.literal("vector3"), x: z.number().finite(), y: z.number().finite(), z: z.number().finite() }).strict(),
  z.object({ kind: z.literal("vector4"), x: z.number().finite(), y: z.number().finite(), z: z.number().finite(), w: z.number().finite() }).strict(),
  z.object({ kind: z.literal("vector2_int"), x: z.number().int(), y: z.number().int() }).strict(),
  z.object({ kind: z.literal("vector3_int"), x: z.number().int(), y: z.number().int(), z: z.number().int() }).strict(),
  z.object({ kind: z.literal("rect"), x: z.number().finite(), y: z.number().finite(), z: z.number().finite(), w: z.number().finite() }).strict(),
  z.object({ kind: z.literal("rect_int"), x: z.number().int(), y: z.number().int(), z: z.number().int(), w: z.number().int() }).strict(),
  z.object({
    kind: z.literal("bounds"),
    x: z.number().finite(),
    y: z.number().finite(),
    z: z.number().finite(),
    x2: z.number().finite(),
    y2: z.number().finite(),
    z2: z.number().finite()
  }).strict(),
  z.object({
    kind: z.literal("bounds_int"),
    x: z.number().int(),
    y: z.number().int(),
    z: z.number().int(),
    x2: z.number().int(),
    y2: z.number().int(),
    z2: z.number().int()
  }).strict(),
  z.object({ kind: z.literal("quaternion"), x: z.number().finite(), y: z.number().finite(), z: z.number().finite(), w: z.number().finite() }).strict(),
  z.object({ kind: z.literal("enum"), enumName: z.string().min(1).max(200) }).strict(),
  z.object({ kind: z.literal("enum"), enumIndex: z.number().int().min(0) }).strict(),
  z.object({ kind: z.literal("object_reference"), assetPath: z.string().min(1).max(512) }).strict(),
  z.object({ kind: z.literal("null") }).strict(),
  z.object({ kind: z.literal("array_size"), integerValue: z.number().int().min(0).max(100000) }).strict(),
  z.object({ kind: z.literal("character"), integerValue: z.number().int().min(0).max(65535) }).strict()
]);

const sceneBatchOperationSchema = z.discriminatedUnion("kind", [
  z.object({
    kind: z.literal("create"),
    name: z.string().min(1).max(80),
    parentPath: z.string().max(512).optional(),
    primitive: z.enum(["empty", "cube", "sphere", "capsule", "cylinder", "plane", "quad"]).default("empty")
  }).strict(),
  z.object({
    kind: z.literal("instantiate_prefab"),
    prefabPath: z.string().min(1).max(512),
    parentPath: z.string().max(512).optional(),
    name: z.string().min(1).max(80).optional()
  }).strict(),
  z.object({ kind: z.literal("delete"), targetPath: z.string().min(1).max(512) }).strict(),
  z.object({
    kind: z.literal("duplicate"),
    targetPath: z.string().min(1).max(512),
    name: z.string().min(1).max(80).optional(),
    parentPath: z.string().max(512).optional(),
    worldPositionStays: z.boolean().default(true)
  }).strict(),
  z.object({
    kind: z.literal("rename"),
    targetPath: z.string().min(1).max(512),
    name: z.string().min(1).max(80)
  }).strict(),
  z.object({
    kind: z.literal("reparent"),
    targetPath: z.string().min(1).max(512),
    parentPath: z.string().max(512).default(""),
    worldPositionStays: z.boolean().default(true)
  }).strict(),
  z.object({
    kind: z.literal("set_active"),
    targetPath: z.string().min(1).max(512),
    active: z.boolean()
  }).strict(),
  z.object({
    kind: z.literal("add_component"),
    targetPath: z.string().min(1).max(512),
    componentType: z.string().min(1).max(256)
  }).strict(),
  z.object({
    kind: z.literal("remove_component"),
    targetPath: z.string().min(1).max(512),
    componentType: z.string().min(1).max(256),
    componentIndex: z.number().int().min(0).default(0)
  }).strict(),
  z.object({
    kind: z.literal("set_property"),
    targetPath: z.string().min(1).max(512),
    componentType: z.string().min(1).max(256),
    componentIndex: z.number().int().min(0).default(0),
    propertyPath: z.string().min(1).max(512),
    value: sceneSerializedValueSchema
  }).strict()
]);

server.registerTool(
  "unity.scene.batch",
  {
    description: "Apply an atomic, undo-backed batch of hierarchy, prefab, component, and serialized-property scene operations.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      operations: z.array(sceneBatchOperationSchema).min(1).max(50)
    }).strict()
  },
  async (input) => bridgeTool("unity.scene.batch", input)
);

server.registerTool(
  "unity.prefabs.list",
  {
    description: "List prefabs in the Unity project with paths, GUIDs, and root component summaries.",
    inputSchema: z.object({
      folder: z.string().default("Assets"),
      maxResults: z.number().int().min(1).max(1000).default(200)
    })
  },
  async ({ folder, maxResults }) => bridgeTool("unity.prefabs.list", { folder, maxResults })
);

server.registerTool(
  "unity.prefab.inspect",
  {
    description: "Inspect a prefab asset hierarchy and components at a high level.",
    inputSchema: z.object({
      path: z.string().min(1),
      includeComponents: z.boolean().default(true),
      maxDepth: z.number().int().min(0).max(10).default(3),
      maxGameObjects: z.number().int().min(1).max(1000).default(200)
    })
  },
  async ({ path, includeComponents, maxDepth, maxGameObjects }) => bridgeTool("unity.prefab.inspect", { path, includeComponents, maxDepth, maxGameObjects })
);

server.registerTool(
  "unity.asset.dependencies",
  {
    description: "Inspect direct or recursive dependencies for a Unity asset path.",
    inputSchema: z.object({
      path: z.string().min(1),
      recursive: z.boolean().default(true),
      maxResults: z.number().int().min(1).max(1000).default(200)
    })
  },
  async ({ path, recursive, maxResults }) => bridgeTool("unity.asset.dependencies", { path, recursive, maxResults })
);

server.registerTool(
  "unity.scripts.list",
  {
    description: "List C# scripts visible to Unity with paths, class names, and namespaces when available.",
    inputSchema: z.object({
      includePackages: z.boolean().default(true),
      maxResults: z.number().int().min(1).max(2000).default(500)
    })
  },
  async ({ includePackages, maxResults }) => bridgeTool("unity.scripts.list", { includePackages, maxResults })
);

server.registerTool(
  "unity.assemblies.list",
  {
    description: "List Unity script assemblies and assembly definition metadata.",
    inputSchema: z.object({
      maxResults: z.number().int().min(1).max(1000).default(500)
    })
  },
  async ({ maxResults }) => bridgeTool("unity.assemblies.list", { maxResults })
);

server.registerTool(
  "unity.packages.list",
  {
    description: "List registered Unity packages for the current project.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.packages.list")
);

server.registerTool(
  "unity.project.settings.inspect",
  {
    description: "Inspect high-level Unity project and player settings.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.project.settings.inspect")
);

server.registerTool(
  "unity.project.settings.update",
  {
    description: "Update selected Project Settings, Android Player Settings, and Build Settings with a durable checkpoint.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      companyName: z.string().min(1).max(256).optional(),
      productName: z.string().min(1).max(256).optional(),
      applicationIdentifier: z.string().min(3).max(256).optional(),
      colorSpace: z.enum(["Gamma", "Linear"]).optional(),
      scriptingBackend: z.enum(["Mono2x", "IL2CPP", "WinRTDotNET"]).optional(),
      androidMinSdk: z.number().int().min(21).max(99).optional(),
      androidTargetSdk: z.number().int().min(0).max(99).optional(),
      androidArchitectures: z.array(z.enum(["ARMv7", "ARM64", "X86"])).min(1).max(3).optional(),
      buildAppBundle: z.boolean().optional(),
      developmentBuild: z.boolean().optional(),
      connectProfiler: z.boolean().optional(),
      scenes: z.array(z.object({
        path: z.string().min(1).max(512),
        enabled: z.boolean().default(true)
      }).strict()).max(200).optional()
    }).strict()
  },
  async (input) => bridgeTool("unity.project.settings.update", input)
);

server.registerTool(
  "unity.packages.change",
  {
    description: "Add or remove registry Unity packages as a durable asynchronous job.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      add: z.array(z.string().regex(/^com\.[A-Za-z0-9._-]+(?:@[A-Za-z0-9._-]+)?$/)).max(50).default([]),
      remove: z.array(z.string().regex(/^com\.[A-Za-z0-9._-]+$/)).max(50).default([])
    }).strict()
  },
  async (input) => bridgeTool("unity.packages.change", input)
);

server.registerTool(
  "unity.jobs.get",
  {
    description: "Get one persistent Unity operation job and its result.",
    inputSchema: z.object({
      jobId: z.string().regex(/^[A-Za-z0-9]{32}$/)
    }).strict()
  },
  async (input) => bridgeTool("unity.jobs.get", input)
);

server.registerTool(
  "unity.jobs.list",
  {
    description: "List persistent Unity operation jobs, optionally filtered by status or kind.",
    inputSchema: z.object({
      status: z.enum(["queued", "running", "succeeded", "failed", "cancelled"]).optional(),
      kind: z.string().min(1).max(64).optional(),
      maxResults: z.number().int().min(1).max(500).default(100)
    }).strict()
  },
  async (input) => bridgeTool("unity.jobs.list", input)
);

server.registerTool(
  "unity.jobs.cancel",
  {
    description: "Request cancellation of a queued or running Unity operation job.",
    inputSchema: z.object({
      jobId: z.string().regex(/^[A-Za-z0-9]{32}$/)
    }).strict()
  },
  async (input) => bridgeTool("unity.jobs.cancel", input)
);

server.registerTool(
  "unity.tests.run",
  {
    description: "Run Unity Edit Mode or Play Mode tests and persist an XML result artifact.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      mode: z.enum(["edit", "play"]).default("edit"),
      testNames: z.array(z.string().min(1).max(512)).max(200).default([]),
      groupNames: z.array(z.string().min(1).max(512)).max(200).default([]),
      categoryNames: z.array(z.string().min(1).max(256)).max(100).default([]),
      assemblyNames: z.array(z.string().min(1).max(256)).max(100).default([]),
      runSynchronously: z.boolean().default(false),
      saveModifiedScenes: z.boolean().default(false)
    }).strict()
  },
  async (input) => bridgeTool("unity.tests.run", input)
);

server.registerTool(
  "unity.playmode.status",
  {
    description: "Read the current Unity Play Mode and pause state.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.playmode.status")
);

server.registerTool(
  "unity.playmode.control",
  {
    description: "Enter, exit, pause, resume, or step Unity Play Mode as a persistent job.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      action: z.enum(["enter", "exit", "pause", "resume", "step"])
    }).strict()
  },
  async (input) => bridgeTool("unity.playmode.control", input)
);

server.registerTool(
  "unity.compilation.status",
  {
    description: "Read Unity compilation/import state and current console error counts.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.compilation.status")
);

server.registerTool(
  "unity.compilation.wait",
  {
    description: "Wait asynchronously for Unity compilation/import to settle and verify the console error count.",
    inputSchema: z.object({
      triggerRefresh: z.boolean().default(true),
      timeoutSeconds: z.number().int().min(5).max(3600).default(300),
      maxErrorCount: z.number().int().min(0).max(100000).default(0)
    }).strict()
  },
  async (input) => bridgeTool("unity.compilation.wait", input)
);

server.registerTool(
  "unity.build.validate_android_quest",
  {
    description: "Validate Android/Quest build support, scenes, player settings, architectures, and XR packages.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.build.validate_android_quest")
);

server.registerTool(
  "unity.build.android",
  {
    description: "Generate a validated Android APK or AAB for Quest as a persistent job.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      label: z.string().min(1).max(50).default("quest"),
      appBundle: z.boolean().default(false),
      development: z.boolean().default(false),
      allowDebugging: z.boolean().default(false),
      connectProfiler: z.boolean().default(false),
      cleanBuildCache: z.boolean().default(false),
      scenes: z.array(z.string().min(1).max(512)).max(200).default([])
    }).strict()
  },
  async (input) => bridgeTool("unity.build.android", input)
);

const materialPropertySchema = z.object({
  name: z.string().min(1).max(256),
  kind: z.enum(["float", "int", "color", "vector", "texture"]),
  numberValue: z.number().finite().optional(),
  integerValue: z.number().int().optional(),
  assetPath: z.string().max(512).optional(),
  x: z.number().finite().optional(),
  y: z.number().finite().optional(),
  z: z.number().finite().optional(),
  w: z.number().finite().optional()
}).strict();

const animationCurveSchema = z.object({
  relativePath: z.string().max(512).default(""),
  componentType: z.string().min(1).max(256).default("UnityEngine.Transform"),
  propertyName: z.string().min(1).max(512),
  keyframes: z.array(z.object({
    time: z.number().finite(),
    value: z.number().finite(),
    inTangent: z.number().finite().default(0),
    outTangent: z.number().finite().default(0)
  }).strict()).min(1).max(10000)
}).strict();

const audioImportSchema = z.object({
  forceToMono: z.boolean().default(false),
  loadInBackground: z.boolean().default(false),
  preloadAudioData: z.boolean().default(true),
  loadType: z.enum(["decompress_on_load", "compressed_in_memory", "streaming"]).default("decompress_on_load"),
  compressionFormat: z.enum(["vorbis", "pcm", "adpcm"]).default("vorbis"),
  quality: z.number().min(0).max(1).default(0.7),
  sampleRateOverride: z.number().int().min(0).max(192000).default(0)
}).strict();

server.registerTool(
  "unity.assets.author",
  {
    description: "Create or edit shaders, materials, animation clips, generated WAV audio, and audio import settings with checkpoints.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      kind: z.enum(["shader", "material", "animation_clip", "audio_tone", "audio_import"]),
      path: z.string().min(1).max(512),
      shaderSource: z.string().max(1_048_576).optional(),
      shaderName: z.string().min(1).max(256).optional(),
      shaderPath: z.string().min(1).max(512).optional(),
      materialProperties: z.array(materialPropertySchema).max(500).default([]),
      enabledKeywords: z.array(z.string().min(1).max(128)).max(200).default([]),
      renderQueue: z.number().int().min(-1).max(5000).default(-1),
      clearExistingCurves: z.boolean().default(false),
      frameRate: z.number().min(1).max(240).default(60),
      animationCurves: z.array(animationCurveSchema).max(500).default([]),
      audioTone: z.object({
        frequencyHz: z.number().min(1).max(86000).default(440),
        durationSeconds: z.number().min(0.01).max(300).default(1),
        sampleRate: z.number().int().min(8000).max(192000).default(44100),
        channels: z.number().int().min(1).max(2).default(1),
        amplitude: z.number().min(0).max(1).default(0.5)
      }).strict().optional(),
      audioImport: audioImportSchema.optional()
    }).strict()
  },
  async (input) => bridgeTool("unity.assets.author", input)
);

const prefabEditSchema = z.object({
  kind: z.enum(["create_child", "delete", "rename", "set_active", "add_component", "remove_component", "set_property"]),
  objectPath: z.string().max(512).default(""),
  name: z.string().min(1).max(80).optional(),
  active: z.boolean().optional(),
  componentType: z.string().min(1).max(256).optional(),
  componentIndex: z.number().int().min(0).default(0),
  propertyPath: z.string().min(1).max(512).optional(),
  value: sceneSerializedValueSchema.optional()
}).strict();

server.registerTool(
  "unity.prefab.manage",
  {
    description: "Save prefab assets, create variants, edit prefab contents, and apply or revert instance overrides with checkpoints.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      action: z.enum(["save_scene_object", "create_variant", "edit_asset", "apply_overrides", "revert_overrides"]),
      prefabPath: z.string().max(512).default(""),
      targetPath: z.string().max(512).default(""),
      sceneObjectPath: z.string().max(512).default(""),
      connectToScene: z.boolean().default(false),
      operations: z.array(prefabEditSchema).max(50).default([])
    }).strict()
  },
  async (input) => bridgeTool("unity.prefab.manage", input)
);

server.registerTool(
  "unity.checkpoints.create",
  {
    description: "Create a durable, hashed checkpoint of selected Unity project paths.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      label: z.string().max(100).optional(),
      paths: z.array(z.string().min(1).max(512)).max(500).default([]),
      maxFiles: z.number().int().min(1).max(100000).default(20000),
      maxBytes: z.number().int().min(1).max(10_737_418_240).default(1_073_741_824)
    }).strict()
  },
  async (input) => bridgeTool("unity.checkpoints.create", input)
);

server.registerTool(
  "unity.checkpoints.list",
  {
    description: "List durable Unity project checkpoints and their manifests.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.checkpoints.list")
);

server.registerTool(
  "unity.checkpoints.restore",
  {
    description: "Restore and hash-verify a durable checkpoint, optionally creating a pre-restore safety checkpoint.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      checkpointId: z.string().min(1).max(128),
      createSafetyCheckpoint: z.boolean().default(true)
    }).strict()
  },
  async (input) => bridgeTool("unity.checkpoints.restore", input)
);

server.registerTool(
  "unity.checkpoints.delete",
  {
    description: "Delete one durable checkpoint after explicit confirmation.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      checkpointId: z.string().min(1).max(128)
    }).strict()
  },
  async (input) => bridgeTool("unity.checkpoints.delete", input)
);

server.registerTool(
  "unity.vision.capture",
  {
    description: "Synchronously capture a Scene View or Game View screenshot and return only after the PNG is verified ready.",
    inputSchema: z.object({
      source: z.enum(["scene", "game"]).default("scene"),
      width: z.number().int().min(1).max(4096).default(640),
      height: z.number().int().min(1).max(4096).default(360),
      label: z.string().min(1).max(50).optional(),
      cameraPath: z.string().min(1).max(512).optional()
    }).strict()
  },
  async (input) => bridgeTool("unity.vision.capture", input)
);

server.registerTool(
  "unity.vision.compare",
  {
    description: "Compare ready before/after screenshot artifacts, generate a diff image, and detect visual regressions.",
    inputSchema: z.object({
      beforePath: z.string().min(1).max(512),
      afterPath: z.string().min(1).max(512),
      pixelThreshold: z.number().min(0).max(1).default(0.1),
      maxChangedPixelRatio: z.number().min(0).max(1).default(0.01),
      maxMeanAbsoluteError: z.number().min(0).max(1).default(0.02),
      ignoreAlpha: z.boolean().default(true),
      generateDiff: z.boolean().default(true),
      label: z.string().min(1).max(50).optional()
    }).strict()
  },
  async (input) => bridgeTool("unity.vision.compare", input)
);

server.registerTool(
  "unity.meta_xr.validate_setup",
  {
    description: "Run initial Meta XR readiness validation through the local Unity Editor bridge.",
    inputSchema: z.object({})
  },
  async () => bridgeTool("unity.meta_xr.validate_setup")
);

server.registerTool(
  "unity.meta_xr.configure",
  {
    description: "Install and configure Unity OpenXR/Meta OpenXR for Quest, including Android target, IL2CPP, ARM64, loader, and features.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false),
      installPackages: z.boolean().default(true),
      switchToAndroid: z.boolean().default(true),
      installMetaOpenXr: z.boolean().default(true),
      androidMinSdk: z.number().int().min(29).max(99).default(29),
      applicationIdentifier: z.string().min(3).max(256).optional()
    }).strict()
  },
  async (input) => bridgeTool("unity.meta_xr.configure", input)
);

server.registerTool(
  "unity.editor.create_empty_game_object",
  {
    description: "Create an empty GameObject in the active Unity scene through a controlled Editor operation.",
    inputSchema: z.object({
      name: z.string().min(1).default("Unity AI GameObject"),
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false)
    })
  },
  async ({ name, dryRun, confirm }) => bridgeTool("unity.editor.create_empty_game_object", { name, dryRun, confirm })
);

server.registerTool(
  "unity.editor.undo_last_operation",
  {
    description: "Undo the last Unity Editor operation through a controlled rollback operation.",
    inputSchema: z.object({
      dryRun: z.boolean().default(true),
      confirm: z.boolean().default(false)
    })
  },
  async ({ dryRun, confirm }) => bridgeTool("unity.editor.undo_last_operation", { dryRun, confirm })
);

async function bridgeTool(capability: string, input: unknown = {}) {
  const response = await bridge.call(capability, input);

  if (!response.ok) {
    return {
      isError: true,
      content: [
        {
          type: "text" as const,
          text: response.error ?? `Unity bridge failed for ${capability}.`
        }
      ]
    };
  }

  return {
    content: [
      {
        type: "text" as const,
        text: response.resultJson ?? JSON.stringify(response, null, 2)
      }
    ]
  };
}

function parseTimeout(value: string | undefined): number {
  const parsed = Number(value ?? 10_000);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 10_000;
}

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Unity AI Control Plane MCP server running on stdio.");
}

main().catch((error) => {
  console.error("Fatal MCP server error:", error);
  process.exit(1);
});
