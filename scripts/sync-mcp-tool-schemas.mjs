#!/usr/bin/env node
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";

const repoRoot = resolve(new URL("..", import.meta.url).pathname);
const serverEntry = join(repoRoot, "apps/mcp-server/dist/index.js");
const generatedManifestPath = join(repoRoot, "apps/mcp-server/generated/tool-schemas.json");
const args = new Set(process.argv.slice(2));

if (!existsSync(serverEntry)) {
  fail(`MCP server build not found: ${serverEntry}. Run npm run build first.`);
}

if (!args.has("--write-repo") && !args.has("--check") && !args.has("--antigravity")) {
  fail("Pass --write-repo, --check, or --antigravity.");
}

const tools = await readTools();
const records = tools
  .map((tool) => ({
    name: tool.name,
    description: tool.description ?? "",
    parameters: requireSchema(tool.name, tool.inputSchema)
  }))
  .sort((left, right) => left.name.localeCompare(right.name));
const manifest = `${JSON.stringify({
  schemaVersion: 1,
  server: "unity-ai-control-plane",
  tools: records
}, null, 2)}\n`;

if (args.has("--check")) {
  if (!existsSync(generatedManifestPath) || readFileSync(generatedManifestPath, "utf8") !== manifest) {
    fail(`Generated tool schemas are stale. Run npm run schemas:generate.`);
  }

  console.log(`Verified ${records.length} generated MCP tool schemas.`);
}

if (args.has("--write-repo")) {
  writeAtomic(generatedManifestPath, manifest);
  console.log(`Generated ${records.length} MCP tool schemas at ${generatedManifestPath}.`);
}

if (args.has("--antigravity")) {
  for (const directory of antigravitySchemaDirectories()) {
    syncSchemaDirectory(directory, records);
    console.log(`Synchronized ${records.length} Antigravity tool schemas in ${directory}.`);
  }
}

async function readTools() {
  const client = new Client({ name: "unity-ai-schema-sync", version: "0.1.0" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverEntry],
    env: process.env,
    stderr: "pipe"
  });

  try {
    await client.connect(transport);
    const result = await client.listTools();
    return result.tools;
  } finally {
    await client.close();
  }
}

function requireSchema(toolName, schema) {
  if (!schema || typeof schema !== "object" || Array.isArray(schema) || schema.type !== "object") {
    fail(`Tool ${toolName} did not publish a valid object inputSchema.`);
  }

  return schema;
}

function antigravitySchemaDirectories() {
  const configured = process.env.UNITY_AI_ANTIGRAVITY_SCHEMA_DIRS;
  if (configured) {
    return configured.split(":").map((value) => resolve(value)).filter(Boolean);
  }

  return [
    join(homedir(), ".gemini/antigravity/mcp/unity-ai"),
    join(homedir(), ".gemini/antigravity-cli/mcp/unity-ai"),
    join(homedir(), ".gemini/antigravity-ide/mcp/unity-ai")
  ];
}

function syncSchemaDirectory(directory, schemaRecords) {
  mkdirSync(directory, { recursive: true });
  const expectedFiles = new Set(schemaRecords.map((record) => `${record.name}.json`));

  for (const fileName of readdirSync(directory)) {
    if (fileName.endsWith(".json") && !expectedFiles.has(fileName)) {
      rmSync(join(directory, fileName));
    }
  }

  for (const record of schemaRecords) {
    writeAtomic(join(directory, `${record.name}.json`), `${JSON.stringify(record)}\n`);
  }
}

function writeAtomic(path, content) {
  mkdirSync(dirname(path), { recursive: true });
  const temporaryPath = `${path}.tmp-${process.pid}`;
  writeFileSync(temporaryPath, content);
  renameSync(temporaryPath, path);
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
