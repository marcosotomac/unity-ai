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
  "unity.vision.capture",
  {
    description: "Capture a Scene View or Game View screenshot through the local Unity Editor bridge.",
    inputSchema: z.object({
      source: z.enum(["scene", "game"]).default("scene")
    })
  },
  async ({ source }) => bridgeTool("unity.vision.capture", { source })
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
