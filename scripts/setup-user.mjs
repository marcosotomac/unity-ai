#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";

const repoRoot = resolve(new URL("..", import.meta.url).pathname);
const unityPackagePath = join(repoRoot, "apps/unity-plugin/Packages/com.unity-ai.control-plane");
const mcpServerPath = join(repoRoot, "apps/mcp-server/dist/index.js");
const defaultBridgeUrl = "http://127.0.0.1:39071";
const opencodeConfigPath = join(homedir(), ".config/opencode/opencode.json");

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  printHelp();
  process.exit(0);
}

if (args.yes) {
  args.write = true;
}

if (!args.unityProject && !args.opencode && !args.build) {
  fail("Nothing to do. Pass --unity-project <path>, --opencode, or --build. Use --help for examples.");
}

if (!existsSync(unityPackagePath)) {
  fail(`Unity package source not found: ${unityPackagePath}`);
}

const bridgeUrl = args.bridgeUrl ?? defaultBridgeUrl;
const generatedToken = args.opencode && args.write && !args.bridgeToken ? generateToken() : undefined;
const bridgeToken = args.bridgeToken ?? generatedToken;
const plannedChanges = [];

if (!args.write) {
  console.log("Dry run: no files will be changed. Pass --write to apply these changes.");
}

if (args.build) {
  runBuild();
}

if (args.unityProject) {
  setupUnityManifest(args.unityProject);
}

if (args.opencode) {
  setupOpencode();
}

if (plannedChanges.length > 0) {
  console.log("\nPlanned changes:");
  for (const change of plannedChanges) {
    console.log(`- ${change}`);
  }
}

if (generatedToken) {
  console.log("\nGenerated local bridge token:");
  console.log(generatedToken);
  console.log("Use the same token when starting the Unity local bridge.");
} else if (args.bridgeToken) {
  console.log(`\nUsing supplied bridge token: ${maskSecret(args.bridgeToken)}`);
}

if (args.write) {
  console.log("\nSetup complete.");
} else {
  console.log("\nDry run complete. Re-run with --write to apply.");
}

function setupUnityManifest(projectPathInput) {
  const projectPath = resolve(projectPathInput);
  const packagesDir = join(projectPath, "Packages");
  const manifestPath = join(packagesDir, "manifest.json");

  assertAllowedRepoTarget(projectPath);

  if (!existsSync(projectPath) || !statSync(projectPath).isDirectory()) {
    fail(`Unity project path does not exist or is not a directory: ${projectPath}`);
  }

  if (!isUnityProjectRoot(projectPath)) {
    fail(`Refusing to create a manifest outside a Unity project root: ${projectPath}`);
  }

  if (!existsSync(packagesDir)) {
    plannedChanges.push(`Create Unity Packages directory: ${packagesDir}`);
    if (args.write) {
      mkdirSync(packagesDir, { recursive: true });
    }
  }

  const manifest = existsSync(manifestPath) ? readJson(manifestPath) : { dependencies: {} };
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    fail(`Unity manifest must be a JSON object: ${manifestPath}`);
  }

  if (!manifest.dependencies || typeof manifest.dependencies !== "object" || Array.isArray(manifest.dependencies)) {
    manifest.dependencies = {};
  }

  const dependencyValue = `file:${unityPackagePath}`;
  const currentValue = manifest.dependencies["com.unity-ai.control-plane"];

  if (currentValue === dependencyValue) {
    plannedChanges.push(`Unity manifest already points to ${dependencyValue}`);
    return;
  }

  manifest.dependencies["com.unity-ai.control-plane"] = dependencyValue;
  plannedChanges.push(`${existsSync(manifestPath) ? "Update" : "Create"} ${manifestPath}`);
  plannedChanges.push(`Set dependencies["com.unity-ai.control-plane"] = "${dependencyValue}"`);

  if (args.write) {
    writeJsonWithBackup(manifestPath, manifest);
  }
}

function setupOpencode() {
  if (!args.build && !existsSync(mcpServerPath)) {
    plannedChanges.push(`Warning: MCP server build output is missing: ${mcpServerPath}. Run npm run build or pass --build before using opencode.`);
  }

  const config = existsSync(opencodeConfigPath) ? readJson(opencodeConfigPath) : {};
  if (!config || typeof config !== "object" || Array.isArray(config)) {
    fail(`opencode config must be a JSON object: ${opencodeConfigPath}`);
  }

  if (!config.$schema) {
    config.$schema = "https://opencode.ai/config.json";
    plannedChanges.push("Add opencode config $schema");
  }

  if (!config.mcp || typeof config.mcp !== "object" || Array.isArray(config.mcp)) {
    config.mcp = {};
  }

  const environment = {
    UNITY_AI_BRIDGE_URL: bridgeUrl
  };

  if (bridgeToken) {
    environment.UNITY_AI_BRIDGE_TOKEN = bridgeToken;
  }

  config.mcp["unity-ai"] = {
    type: "local",
    command: ["node", mcpServerPath],
    enabled: true,
    environment
  };

  plannedChanges.push(`Add/update opencode MCP server "unity-ai" in ${opencodeConfigPath}`);
  plannedChanges.push(`Set command to ["node", "${mcpServerPath}"]`);
  plannedChanges.push(`Set UNITY_AI_BRIDGE_URL = "${bridgeUrl}"`);
  if (bridgeToken) {
    plannedChanges.push(`Set UNITY_AI_BRIDGE_TOKEN = "${maskSecret(bridgeToken)}"`);
  } else if (args.opencode) {
    plannedChanges.push("Generate and set a local UNITY_AI_BRIDGE_TOKEN when run with --write");
  }

  if (args.write) {
    mkdirSync(dirname(opencodeConfigPath), { recursive: true });
    writeJsonWithBackup(opencodeConfigPath, config);
  }
}

function runBuild() {
  const nodeModulesPath = join(repoRoot, "node_modules");
  if (!existsSync(nodeModulesPath)) {
    fail("node_modules is missing. Run npm ci first, then re-run setup with --build.");
  }

  console.log("Running npm run build...");
  const result = spawnSync("npm", ["run", "build"], {
    cwd: repoRoot,
    stdio: "inherit"
  });

  if (result.status !== 0) {
    fail("npm run build failed.");
  }
}

function isUnityProjectRoot(projectPath) {
  return existsSync(join(projectPath, "Assets")) || existsSync(join(projectPath, "ProjectSettings")) || existsSync(join(projectPath, "Packages"));
}

function assertAllowedRepoTarget(targetPath) {
  const forbiddenPaths = [join(repoRoot, ".pi"), join(repoRoot, "artifacts"), join(repoRoot, ".unity-ai")];
  const absoluteTargetPath = resolve(targetPath);

  for (const forbiddenPath of forbiddenPaths) {
    if (absoluteTargetPath === forbiddenPath || absoluteTargetPath.startsWith(`${forbiddenPath}/`)) {
      fail(`Refusing to write setup output inside protected path: ${forbiddenPath}`);
    }
  }
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    fail(`Failed to read JSON from ${path}: ${error.message}`);
  }
}

function writeJsonWithBackup(path, value) {
  if (existsSync(path)) {
    const backupPath = `${path}.bak-${timestamp()}`;
    writeFileSync(backupPath, readFileSync(path));
    plannedChanges.push(`Backed up ${path} to ${backupPath}`);
  }

  const json = `${JSON.stringify(value, null, 2)}\n`;
  JSON.parse(json);
  writeFileSync(path, json, "utf8");
  JSON.parse(readFileSync(path, "utf8"));
}

function parseArgs(argv) {
  const parsed = {
    build: false,
    help: false,
    opencode: false,
    write: false,
    yes: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    switch (arg) {
      case "--build":
        parsed.build = true;
        break;
      case "--bridge-token":
        parsed.bridgeToken = readValue(argv, index, arg);
        index += 1;
        break;
      case "--bridge-url":
        parsed.bridgeUrl = readValue(argv, index, arg);
        index += 1;
        break;
      case "--help":
      case "-h":
        parsed.help = true;
        break;
      case "--opencode":
        parsed.opencode = true;
        break;
      case "--unity-project":
        parsed.unityProject = readValue(argv, index, arg);
        index += 1;
        break;
      case "--write":
        parsed.write = true;
        break;
      case "--yes":
        parsed.yes = true;
        break;
      default:
        fail(`Unknown argument: ${arg}`);
    }
  }

  return parsed;
}

function readValue(argv, index, flag) {
  const value = argv[index + 1];
  if (!value || value.startsWith("--")) {
    fail(`Missing value for ${flag}`);
  }
  return value;
}

function generateToken() {
  return randomBytes(24).toString("base64url");
}

function maskSecret(secret) {
  if (secret.length <= 8) {
    return "****";
  }
  return `${secret.slice(0, 4)}...${secret.slice(-4)}`;
}

function timestamp() {
  const now = new Date();
  const pad = (value) => String(value).padStart(2, "0");
  return `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
}

function printHelp() {
  console.log(`Unity AI user setup

Usage:
  npm run setup:user -- --opencode
  npm run setup:user -- --unity-project /path/to/UnityProject
  npm run setup:user -- --opencode --unity-project /path/to/UnityProject --build --write

Options:
  --unity-project <path>  Add/update the Unity package file dependency in Packages/manifest.json.
  --opencode             Add/update the global opencode MCP server entry.
  --write                Apply changes. Without this flag, setup runs as a dry run.
  --yes                  Alias for --write.
  --build                Run npm run build before configuring opencode.
  --bridge-url <url>     Unity local bridge URL. Default: ${defaultBridgeUrl}
  --bridge-token <token> Unity local bridge token. If omitted with --opencode, one is generated.
  --help                 Show this help.
`);
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
