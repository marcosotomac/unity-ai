#!/usr/bin/env node
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { existsSync, rmSync, writeFileSync, mkdirSync, cpSync, readFileSync, symlinkSync } from "node:fs";
import { join, resolve } from "node:path";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";

const repoRoot = resolve(new URL("..", import.meta.url).pathname);
const packageSource = join(repoRoot, "apps/unity-plugin/Packages/com.unity-ai.control-plane");
const serverEntry = join(repoRoot, "apps/mcp-server/dist/index.js");
const unityPath = process.env.UNITY_PATH ?? process.argv[2];
const bridgeUrl = process.env.UNITY_AI_BRIDGE_URL ?? "http://127.0.0.1:39071";
const bridgeToken = process.env.UNITY_AI_BRIDGE_TOKEN ?? randomUUID();
const clean = process.env.UNITY_AI_E2E_CLEAN === "1" || process.argv.includes("--clean");
const tempProject = join(repoRoot, ".unity-ai/e2e-project");
const packagesDir = join(tempProject, "Packages");
const assetsDir = join(tempProject, "Assets");
const projectSettingsDir = join(tempProject, "ProjectSettings");
const artifactsDir = join(repoRoot, "artifacts/unity-verification");
const readyFile = join(artifactsDir, "bridge-ready.txt");
const tokenFile = join(artifactsDir, "bridge-token.txt");
const logPath = join(artifactsDir, "mcp-unity-e2e.log");
const diagnosticWarningMarker = "UNITY_AI_E2E_DIAGNOSTIC_WARNING";
const diagnosticErrorMarker = "UNITY_AI_E2E_DIAGNOSTIC_ERROR";
const diagnosticExceptionMarker = "UNITY_AI_E2E_DIAGNOSTIC_EXCEPTION";
const applyFixFile = "Assets/UnityAiE2EApplyFixFixture.cs";
const applyFixOriginalLine = "    public const string Marker = \"before\";";
const applyFixReplacementLine = "    public const string Marker = \"after\";";

if (!unityPath || !existsSync(unityPath)) {
  fail(`Unity executable not found. Set UNITY_PATH or pass it as the first argument.`);
}

if (!existsSync(serverEntry)) {
  fail(`MCP server build not found: ${serverEntry}. Run npm run build first.`);
}

if (clean) {
  rmSync(tempProject, { recursive: true, force: true });
}

mkdirSync(packagesDir, { recursive: true });
mkdirSync(assetsDir, { recursive: true });
mkdirSync(join(assetsDir, "Editor"), { recursive: true });
mkdirSync(projectSettingsDir, { recursive: true });
mkdirSync(artifactsDir, { recursive: true });
rmSync(readyFile, { force: true });
rmSync(tokenFile, { force: true });
rmSync(join(packagesDir, "com.unity-ai.control-plane"), { recursive: true, force: true });
writeFileSync(tokenFile, bridgeToken);
cpSync(packageSource, join(packagesDir, "com.unity-ai.control-plane"), { recursive: true });

writeFileSync(
  join(packagesDir, "manifest.json"),
  JSON.stringify(
    {
      dependencies: {
        "com.unity-ai.control-plane": "file:com.unity-ai.control-plane"
      }
    },
    null,
    2
  )
);

writeFileSync(join(projectSettingsDir, "ProjectVersion.txt"), "m_EditorVersion: 6000.4.9f1\n");
writeFileSync(
  join(tempProject, applyFixFile),
  `public static class UnityAiE2EApplyFixFixture
{
${applyFixOriginalLine}
}
`
);
writeFileSync(
  join(assetsDir, "Editor/UnityAiE2EConsoleDiagnosticsFixture.cs"),
  `using System;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class UnityAiE2EConsoleDiagnosticsFixture
{
    static UnityAiE2EConsoleDiagnosticsFixture()
    {
        EditorApplication.delayCall += EmitDiagnostics;
    }

    private static void EmitDiagnostics()
    {
        Debug.LogWarning("${diagnosticWarningMarker} uses project root " + Application.dataPath + " and Windows root C:/Users/example/AppData/Local/Temp/unity-ai-warning.txt");
        Debug.LogError("error CS9999: ${diagnosticErrorMarker} uses project root " + Application.dataPath + " and Unix root /tmp/unity-ai-error.txt");
        Debug.LogException(new InvalidOperationException("${diagnosticExceptionMarker} uses project root " + Application.dataPath + " and macOS root /Users/example/unity-ai-exception.txt"));
    }
}
`
);

const unity = spawn(unityPath, [
  "-batchmode",
  "-nographics",
  "-projectPath",
  tempProject,
  "-executeMethod",
  "UnityAI.ControlPlane.Editor.UnityAiBridgeBatchRunner.Run",
  "-unityAiReadyFile",
  readyFile,
  "-unityAiDurationSeconds",
  "240",
  "-unityAiBridgeTokenFile",
  tokenFile,
  "-logFile",
  logPath
], { stdio: "inherit" });

try {
  await waitForFile(readyFile, 60_000);
  await waitForBridgeHealth(bridgeUrl, 60_000);
  await assertMutatingRouteRequiresToken(bridgeUrl);
  await waitForBridgeCapability(bridgeUrl, "unity.project.inspect", 180_000);

  const client = new Client({ name: "unity-ai-e2e", version: "0.1.0" });
  const transport = new StdioClientTransport({
    command: "node",
    args: [serverEntry],
    env: {
      ...process.env,
      UNITY_AI_BRIDGE_URL: bridgeUrl,
      UNITY_AI_BRIDGE_TOKEN: bridgeToken
    }
  });

  await client.connect(transport);

  assertCapabilities(await callJsonTool(client, "unity.capabilities.list", {}));
  await assertApplyFixFlow(client);
  const before = await callJsonTool(client, "unity.project.inspect", {});
  await callJsonTool(client, "unity.console.read", {});
  assertConsoleDiagnostics(await waitForConsoleDiagnostics(client, 60_000));
  assertConsoleFixPlans(await callJsonTool(client, "unity.console.plan_fix", {}));
  assertProjectSnapshot(await callJsonTool(client, "unity.project.snapshot", {}));
  assertAssetList(await callJsonTool(client, "unity.assets.list", { folder: "Assets", maxResults: 50 }));
  assertSceneList(await callJsonTool(client, "unity.scenes.list", {}));
  assertSceneInspect(await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 3, maxGameObjects: 50 }));
  assertPrefabList(await callJsonTool(client, "unity.prefabs.list", { folder: "Assets", maxResults: 50 }));
  assertPrefabInspect(await callJsonTool(client, "unity.prefab.inspect", { path: "Assets/UnityAiE2E.prefab", includeComponents: true, maxDepth: 3, maxGameObjects: 50 }), true);
  assertPrefabInspect(await callJsonTool(client, "unity.prefab.inspect", { path: "Assets/NoPrefabHere.prefab", includeComponents: true, maxDepth: 3, maxGameObjects: 50 }), false);
  assertAssetDependencies(await callJsonTool(client, "unity.asset.dependencies", { path: "Packages/com.unity-ai.control-plane/package.json", recursive: true, maxResults: 50 }));
  assertScriptList(await callJsonTool(client, "unity.scripts.list", { includePackages: true, maxResults: 500 }));
  assertAssemblyList(await callJsonTool(client, "unity.assemblies.list", { maxResults: 500 }));
  assertPackageList(await callJsonTool(client, "unity.packages.list", {}));
  assertProjectSettings(await callJsonTool(client, "unity.project.settings.inspect", {}));
  await callJsonTool(client, "unity.meta_xr.validate_setup", {});
  await callJsonTool(client, "unity.vision.capture", { source: "scene" });

  const dryRun = await callJsonTool(client, "unity.editor.create_empty_game_object", {
    name: "Unity AI E2E Dry Run",
    dryRun: true
  });
  assertCreateResult("dry-run create", dryRun, {
    capability: "unity.editor.create_empty_game_object",
    dryRun: true,
    created: false,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["scene_mutation_verified"]
  });

  const needsConfirmation = await callJsonTool(client, "unity.editor.create_empty_game_object", {
    name: "Unity AI E2E Needs Confirmation",
    dryRun: false,
    confirm: false
  });
  assertCreateResult("confirmation gate", needsConfirmation, {
    capability: "unity.editor.create_empty_game_object",
    dryRun: false,
    created: false,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["scene_mutation_verified"],
    verificationStatus: "needs_confirmation",
    requiresConfirmation: true
  });

  const created = await callJsonTool(client, "unity.editor.create_empty_game_object", {
    name: "Unity AI E2E Object",
    dryRun: false,
    confirm: true
  });
  assertCreateResult("real create", created, {
    capability: "unity.editor.create_empty_game_object",
    dryRun: false,
    created: true,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "scene_mutation_verified"],
    forbiddenSignals: [],
    verificationStatus: "passed",
    requiresConfirmation: false
  });

  const after = await callJsonTool(client, "unity.project.inspect", {});
  if (after.rootGameObjectCount !== before.rootGameObjectCount + 1) {
    fail(`Final project inspection expected root count ${before.rootGameObjectCount + 1}, got ${after.rootGameObjectCount}.`);
  }

  if (!Array.isArray(after.rootGameObjectNames) || !after.rootGameObjectNames.includes("Unity AI E2E Object")) {
    fail("Final project inspection expected created object name to be present.");
  }

  const undoDryRun = await callJsonTool(client, "unity.editor.undo_last_operation", {
    dryRun: true
  });
  assertUndoResult("dry-run undo", undoDryRun, {
    dryRun: true,
    undone: false,
    beforeCount: before.rootGameObjectCount + 1,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["rollback_verified"]
  });

  const undoNeedsConfirmation = await callJsonTool(client, "unity.editor.undo_last_operation", {
    dryRun: false,
    confirm: false
  });
  assertUndoResult("undo confirmation gate", undoNeedsConfirmation, {
    dryRun: false,
    undone: false,
    beforeCount: before.rootGameObjectCount + 1,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["rollback_verified"],
    verificationStatus: "needs_confirmation",
    requiresConfirmation: true
  });

  const undone = await callJsonTool(client, "unity.editor.undo_last_operation", {
    dryRun: false,
    confirm: true
  });
  assertUndoResult("real undo", undone, {
    dryRun: false,
    undone: true,
    beforeCount: before.rootGameObjectCount + 1,
    afterCount: before.rootGameObjectCount,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "rollback_verified"],
    forbiddenSignals: [],
    verificationStatus: "passed",
    requiresConfirmation: false
  });

  const afterUndo = await callJsonTool(client, "unity.project.inspect", {});
  if (afterUndo.rootGameObjectCount !== before.rootGameObjectCount) {
    fail(`Post-undo project inspection expected root count ${before.rootGameObjectCount}, got ${afterUndo.rootGameObjectCount}.`);
  }

  if (Array.isArray(afterUndo.rootGameObjectNames) && afterUndo.rootGameObjectNames.includes("Unity AI E2E Object")) {
    fail("Post-undo project inspection expected created object name to be absent.");
  }

  await client.close();
  console.log(`MCP ↔ Unity end-to-end verification passed. Unity log: ${logPath}`);
} finally {
  unity.kill("SIGTERM");
  await waitForProcessExit(unity, 10_000);
  rmSync(tokenFile, { force: true });
}

async function callTextTool(client, name, args) {
  const result = await client.callTool({ name, arguments: args });
  assertTextResult(name, result);
  console.log(`✓ ${name}`);
  return getTextContent(name, result);
}

async function callJsonTool(client, name, args) {
  const text = await callTextTool(client, name, args);

  try {
    return JSON.parse(text);
  } catch (error) {
    fail(`Tool ${name} did not return valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function assertTextResult(toolName, result) {
  if (!result?.content?.some((item) => item.type === "text" && item.text.length > 0)) {
    fail(`Tool ${toolName} did not return non-empty text content.`);
  }
}

function assertCapabilities(capabilities) {
  if (!Array.isArray(capabilities) || capabilities.length === 0) {
    fail("unity.capabilities.list returned an invalid capability list shape.");
  }

  const diagnosticCapability = capabilities.find((capability) => capability.name === "unity.console.diagnose");
  if (!diagnosticCapability) {
    fail("unity.capabilities.list did not include unity.console.diagnose.");
  }

  const fixPlanCapability = capabilities.find((capability) => capability.name === "unity.console.plan_fix");
  if (!fixPlanCapability) {
    fail("unity.capabilities.list did not include unity.console.plan_fix.");
  }

  if (!Array.isArray(diagnosticCapability.permissions) || !diagnosticCapability.permissions.includes("read_console")) {
    fail("unity.console.diagnose capability must declare read_console permission.");
  }

  if (!Array.isArray(diagnosticCapability.effects) || !diagnosticCapability.effects.includes("report_only")) {
    fail("unity.console.diagnose capability must remain report_only.");
  }

  if (!Array.isArray(fixPlanCapability.permissions) || !fixPlanCapability.permissions.includes("read_console")) {
    fail("unity.console.plan_fix capability must declare read_console permission.");
  }

  if (!Array.isArray(fixPlanCapability.effects) || !fixPlanCapability.effects.includes("report_only")) {
    fail("unity.console.plan_fix capability must remain report_only.");
  }

  if (!Array.isArray(fixPlanCapability.verification) || !fixPlanCapability.verification.includes("fix_plan_generated")) {
    fail("unity.console.plan_fix capability must declare fix_plan_generated verification.");
  }

  const applyFixCapability = capabilities.find((capability) => capability.name === "unity.console.apply_fix");
  if (!applyFixCapability) {
    fail("unity.capabilities.list did not include unity.console.apply_fix.");
  }

  const snapshotCapability = capabilities.find((capability) => capability.name === "unity.project.snapshot");
  if (!snapshotCapability) {
    fail("unity.capabilities.list did not include unity.project.snapshot.");
  }

  if (!Array.isArray(snapshotCapability.permissions) || !snapshotCapability.permissions.includes("read_project") || !snapshotCapability.permissions.includes("read_console")) {
    fail("unity.project.snapshot capability must declare read_project and read_console permissions.");
  }

  if (!Array.isArray(snapshotCapability.effects) || JSON.stringify(snapshotCapability.effects) !== JSON.stringify(["report_only"])) {
    fail("unity.project.snapshot capability must remain report_only.");
  }

  if (!Array.isArray(snapshotCapability.verification) || !snapshotCapability.verification.includes("structured_observation") || !snapshotCapability.verification.includes("console_diagnostics")) {
    fail("unity.project.snapshot capability must declare observation and console diagnostic verification.");
  }

  if (!Array.isArray(applyFixCapability.permissions) || !applyFixCapability.permissions.includes("modify_assets")) {
    fail("unity.console.apply_fix capability must declare modify_assets permission.");
  }

  if (!Array.isArray(applyFixCapability.effects) || !applyFixCapability.effects.includes("write_checkpoint") || !applyFixCapability.effects.includes("asset_change")) {
    fail("unity.console.apply_fix capability must declare checkpoint and asset change effects.");
  }

  if (!Array.isArray(applyFixCapability.verification) || !applyFixCapability.verification.includes("line_replacement_verified")) {
    fail("unity.console.apply_fix capability must declare line_replacement_verified verification.");
  }
}

function getTextContent(toolName, result) {
  const item = result.content.find((contentItem) => contentItem.type === "text" && contentItem.text.length > 0);

  if (!item) {
    fail(`Tool ${toolName} did not return text content.`);
  }

  return item.text;
}

function assertCreateResult(label, result, expected) {
  if (result.dryRun !== expected.dryRun) {
    fail(`${label}: expected dryRun=${expected.dryRun}, got ${result.dryRun}.`);
  }

  if (result.created !== expected.created) {
    fail(`${label}: expected created=${expected.created}, got ${result.created}.`);
  }

  if (result.rootGameObjectCountBefore !== expected.beforeCount) {
    fail(`${label}: expected before count ${expected.beforeCount}, got ${result.rootGameObjectCountBefore}.`);
  }

  if (result.rootGameObjectCountAfter !== expected.afterCount) {
    fail(`${label}: expected after count ${expected.afterCount}, got ${result.rootGameObjectCountAfter}.`);
  }

  if (typeof result.audit !== "string" || result.audit.length === 0) {
    fail(`${label}: expected non-empty audit string.`);
  }

  if (typeof result.verification !== "string" || result.verification.length === 0) {
    fail(`${label}: expected non-empty verification string.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1) {
    fail(`${label}: expected exactly one structured audit event.`);
  }

  const [auditEvent] = result.auditEvents;

  if (auditEvent.capability !== expected.capability) {
    fail(`${label}: expected audit event capability ${expected.capability}, got ${auditEvent.capability}.`);
  }

  if (typeof result.requestId !== "string" || result.requestId.length === 0) {
    fail(`${label}: expected non-empty result requestId.`);
  }

  if (typeof result.correlationId !== "string" || result.correlationId.length === 0) {
    fail(`${label}: expected non-empty result correlationId.`);
  }

  if (auditEvent.requestId !== result.requestId) {
    fail(`${label}: expected audit requestId ${result.requestId}, got ${auditEvent.requestId}.`);
  }

  if (auditEvent.correlationId !== result.correlationId) {
    fail(`${label}: expected audit correlationId ${result.correlationId}, got ${auditEvent.correlationId}.`);
  }

  if (!Array.isArray(auditEvent.effects) || auditEvent.effects.length === 0) {
    fail(`${label}: expected audit event effects.`);
  }

  if (JSON.stringify(auditEvent.effects) !== JSON.stringify(expected.effects)) {
    fail(`${label}: expected audit effects ${JSON.stringify(expected.effects)}, got ${JSON.stringify(auditEvent.effects)}.`);
  }

  if (Number.isNaN(Date.parse(auditEvent.timestamp))) {
    fail(`${label}: expected audit timestamp to be ISO-parseable, got ${auditEvent.timestamp}.`);
  }

  if (Number.isNaN(Date.parse(result.timestampUtc))) {
    fail(`${label}: expected result timestampUtc to be ISO-parseable, got ${result.timestampUtc}.`);
  }

  const expectedVerificationStatus = expected.verificationStatus ?? "passed";
  if (result.verificationStatus !== expectedVerificationStatus) {
    fail(`${label}: expected verificationStatus ${expectedVerificationStatus}, got ${result.verificationStatus}.`);
  }

  const expectedRequiresConfirmation = expected.requiresConfirmation ?? false;
  if (result.requiresConfirmation !== expectedRequiresConfirmation) {
    fail(`${label}: expected requiresConfirmation ${expectedRequiresConfirmation}, got ${result.requiresConfirmation}.`);
  }

  if (!Array.isArray(result.requiredPermissions) || !result.requiredPermissions.includes("modify_scenes")) {
    fail(`${label}: expected requiredPermissions to include modify_scenes.`);
  }

  if (result.auditPersisted !== true) {
    fail(`${label}: expected auditPersisted=true, got ${result.auditPersisted}.`);
  }

  if (typeof result.auditLogPath !== "string" || result.auditLogPath.length === 0 || result.auditLogPath.startsWith("/")) {
    fail(`${label}: expected auditLogPath to be project-relative, got ${result.auditLogPath}.`);
  }

  if (typeof result.auditLogAbsolutePath !== "string" || result.auditLogAbsolutePath.length === 0 || !existsSync(result.auditLogAbsolutePath)) {
    fail(`${label}: expected auditLogAbsolutePath to point to an existing file, got ${result.auditLogAbsolutePath}.`);
  }

  assertAuditLogContains(label, result.auditLogAbsolutePath, auditEvent);

  if (!Array.isArray(result.verificationSignals)) {
    fail(`${label}: expected verificationSignals array.`);
  }

  for (const signal of expected.requiredSignals) {
    if (!result.verificationSignals.includes(signal)) {
      fail(`${label}: expected verificationSignals to include ${signal}.`);
    }
  }

  for (const signal of expected.forbiddenSignals) {
    if (result.verificationSignals.includes(signal)) {
      fail(`${label}: did not expect verificationSignals to include ${signal}.`);
    }
  }
}

function assertUndoResult(label, result, expected) {
  if (result.dryRun !== expected.dryRun) {
    fail(`${label}: expected dryRun=${expected.dryRun}, got ${result.dryRun}.`);
  }

  if (result.undone !== expected.undone) {
    fail(`${label}: expected undone=${expected.undone}, got ${result.undone}.`);
  }

  assertOperationCommon(label, result, {
    ...expected,
    capability: "unity.editor.undo_last_operation"
  });
}

async function assertApplyFixFlow(client) {
  const targetLine = findLineNumber(join(tempProject, applyFixFile), applyFixOriginalLine);

  await assertApplyFixRefused(client, "embedded Assets path", {
    targetFile: `foo/${applyFixFile}`,
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });

  await assertApplyFixRefused(client, "absolute path", {
    targetFile: join(tempProject, applyFixFile),
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });

  await assertApplyFixRefused(client, "parent traversal", {
    targetFile: "Assets/../ProjectSettings/ProjectVersion.txt",
    targetLine: 1,
    expectedOriginalLine: "x",
    replacementLine: "y"
  });

  await assertApplyFixRefused(client, "non C# asset", {
    targetFile: "Assets/not-code.txt",
    targetLine: 1,
    expectedOriginalLine: "x",
    replacementLine: "y"
  });

  await assertApplyFixRefused(client, "multiline replacement", {
    targetFile: applyFixFile,
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: `${applyFixReplacementLine}\n// second line`
  });

  await assertApplyFixRefused(client, "line mismatch", {
    targetFile: applyFixFile,
    targetLine,
    expectedOriginalLine: "    public const string Marker = \"wrong\";",
    replacementLine: applyFixReplacementLine
  });

  const symlinkTarget = join(tempProject, "UnityAiSymlinkEscapeTarget.cs");
  const symlinkFile = join(tempProject, "Assets/UnityAiSymlinkEscape.cs");
  writeFileSync(symlinkTarget, `${applyFixOriginalLine}\n`, "utf8");
  rmSync(symlinkFile, { force: true });
  symlinkSync(symlinkTarget, symlinkFile);
  await assertApplyFixRefused(client, "symlink escape", {
    dryRun: false,
    confirm: true,
    targetFile: "Assets/UnityAiSymlinkEscape.cs",
    targetLine: 1,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });
  assertFileContainsLine("symlink escape", symlinkTarget, applyFixOriginalLine);
  rmSync(symlinkFile, { force: true });
  rmSync(symlinkTarget, { force: true });

  const dryRun = await callJsonTool(client, "unity.console.apply_fix", {
    targetFile: applyFixFile,
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });
  assertApplyFixResult("dry-run apply_fix", dryRun, {
    dryRun: true,
    applied: false,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["checkpoint_created", "line_replacement_verified"],
    verificationStatus: "passed",
    requiresConfirmation: false,
    checkpointCreated: false
  });
  assertFileContainsLine("dry-run apply_fix", join(tempProject, applyFixFile), applyFixOriginalLine);

  const needsConfirmation = await callJsonTool(client, "unity.console.apply_fix", {
    dryRun: false,
    confirm: false,
    targetFile: applyFixFile,
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });
  assertApplyFixResult("confirmation gate apply_fix", needsConfirmation, {
    dryRun: false,
    applied: false,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["checkpoint_created", "line_replacement_verified"],
    verificationStatus: "needs_confirmation",
    requiresConfirmation: true,
    checkpointCreated: false
  });
  assertFileContainsLine("confirmation gate apply_fix", join(tempProject, applyFixFile), applyFixOriginalLine);

  const applied = await callJsonTool(client, "unity.console.apply_fix", {
    dryRun: false,
    confirm: true,
    targetFile: applyFixFile,
    targetLine,
    expectedOriginalLine: applyFixOriginalLine,
    replacementLine: applyFixReplacementLine
  });
  assertApplyFixResult("real apply_fix", applied, {
    dryRun: false,
    applied: true,
    effects: ["asset_change", "write_checkpoint", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "checkpoint_created", "line_replacement_verified"],
    forbiddenSignals: [],
    verificationStatus: "passed",
    requiresConfirmation: false,
    checkpointCreated: true
  });
  assertFileContainsLine("real apply_fix", join(tempProject, applyFixFile), applyFixReplacementLine);
}

async function assertApplyFixRefused(client, label, input) {
  const result = await callJsonTool(client, "unity.console.apply_fix", input);
  if (result.applied !== false || result.refused !== true || result.verificationStatus !== "refused") {
    fail(`${label}: expected apply_fix refusal, got ${JSON.stringify(result)}.`);
  }

  assertNoAbsolutePathLeakInValue(label, result);

  if (result.checkpointCreated || result.checkpointPath) {
    fail(`${label}: refused apply_fix must not create a checkpoint.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1 || result.auditEvents[0].capability !== "unity.console.apply_fix") {
    fail(`${label}: refused apply_fix must return one structured audit event.`);
  }
}

function assertApplyFixResult(label, result, expected) {
  if (result.dryRun !== expected.dryRun || result.applied !== expected.applied) {
    fail(`${label}: expected dryRun=${expected.dryRun} applied=${expected.applied}, got dryRun=${result.dryRun} applied=${result.applied}.`);
  }

  if (result.targetFile !== applyFixFile || typeof result.targetLine !== "number") {
    fail(`${label}: expected relative target file and numeric line.`);
  }

  assertNoAbsolutePathLeakInValue(label, result);

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1) {
    fail(`${label}: expected exactly one structured audit event.`);
  }

  const [auditEvent] = result.auditEvents;
  if (auditEvent.capability !== "unity.console.apply_fix") {
    fail(`${label}: expected audit event capability unity.console.apply_fix, got ${auditEvent.capability}.`);
  }

  if (JSON.stringify(auditEvent.effects) !== JSON.stringify(expected.effects)) {
    fail(`${label}: expected audit effects ${JSON.stringify(expected.effects)}, got ${JSON.stringify(auditEvent.effects)}.`);
  }

  if (auditEvent.requestId !== result.requestId || auditEvent.correlationId !== result.correlationId) {
    fail(`${label}: expected audit request/correlation ids to match result.`);
  }

  if (result.verificationStatus !== expected.verificationStatus || result.requiresConfirmation !== expected.requiresConfirmation) {
    fail(`${label}: expected status ${expected.verificationStatus} requiresConfirmation=${expected.requiresConfirmation}, got ${result.verificationStatus} requiresConfirmation=${result.requiresConfirmation}.`);
  }

  if (result.checkpointCreated !== expected.checkpointCreated) {
    fail(`${label}: expected checkpointCreated=${expected.checkpointCreated}, got ${result.checkpointCreated}.`);
  }

  if (expected.checkpointCreated) {
    if (typeof result.checkpointPath !== "string" || result.checkpointPath.startsWith("/") || !existsSync(join(tempProject, result.checkpointPath))) {
      fail(`${label}: expected relative checkpointPath to exist, got ${result.checkpointPath}.`);
    }
  }

  if (!Array.isArray(result.requiredPermissions) || !result.requiredPermissions.includes("modify_assets")) {
    fail(`${label}: expected requiredPermissions to include modify_assets.`);
  }

  if (result.auditPersisted !== true || typeof result.auditLogPath !== "string" || result.auditLogPath.startsWith("/")) {
    fail(`${label}: expected persisted relative audit log path.`);
  }

  assertAuditLogContains(label, join(tempProject, result.auditLogPath), auditEvent);

  for (const signal of expected.requiredSignals) {
    if (!result.verificationSignals.includes(signal)) {
      fail(`${label}: expected verificationSignals to include ${signal}.`);
    }
  }

  for (const signal of expected.forbiddenSignals) {
    if (result.verificationSignals.includes(signal)) {
      fail(`${label}: did not expect verificationSignals to include ${signal}.`);
    }
  }
}

function findLineNumber(path, expectedLine) {
  const lines = readFileSync(path, "utf8").split(/\r?\n/);
  const index = lines.findIndex((line) => line === expectedLine);
  if (index < 0) {
    fail(`Could not find apply_fix fixture line: ${expectedLine}`);
  }

  return index + 1;
}

function assertFileContainsLine(label, path, expectedLine) {
  if (!readFileSync(path, "utf8").split(/\r?\n/).includes(expectedLine)) {
    fail(`${label}: expected file to contain ${expectedLine}`);
  }
}

function assertAssetList(report) {
  if (typeof report.totalFound !== "number" || typeof report.returned !== "number" || !Array.isArray(report.assets)) {
    fail("unity.assets.list returned an invalid asset list shape.");
  }

  if (report.returned > 50) {
    fail(`unity.assets.list returned more assets than requested: ${report.returned}.`);
  }

  if (report.assets.some((asset) => typeof asset.path !== "string" || !asset.path.startsWith("Assets"))) {
    fail("unity.assets.list returned an asset outside the requested Assets folder.");
  }
}

function assertSceneList(report) {
  if (typeof report.totalFound !== "number" || typeof report.buildSettingsCount !== "number" || typeof report.returned !== "number" || !Array.isArray(report.scenes)) {
    fail("unity.scenes.list returned an invalid scene list shape.");
  }

  if (report.returned > 500) {
    fail(`unity.scenes.list returned more scenes than cap: ${report.returned}.`);
  }
}

function assertSceneInspect(report) {
  if (typeof report.rootGameObjectCount !== "number" || typeof report.returnedGameObjectCount !== "number" || typeof report.truncatedByDepth !== "boolean" || typeof report.truncatedByCount !== "boolean" || !Array.isArray(report.gameObjects)) {
    fail("unity.scene.inspect returned an invalid scene inspect shape.");
  }

  if (report.returnedGameObjectCount > 50) {
    fail(`unity.scene.inspect returned more GameObjects than requested: ${report.returnedGameObjectCount}.`);
  }
}

function assertPrefabList(report) {
  if (typeof report.totalFound !== "number" || typeof report.returned !== "number" || typeof report.truncated !== "boolean" || !Array.isArray(report.prefabs)) {
    fail("unity.prefabs.list returned an invalid prefab list shape.");
  }

  if (report.returned > 50) {
    fail(`unity.prefabs.list returned more prefabs than requested: ${report.returned}.`);
  }

  if (report.prefabs.some((prefab) => typeof prefab.path !== "string" || !prefab.path.startsWith("Assets"))) {
    fail("unity.prefabs.list returned a prefab outside the requested Assets folder.");
  }

  if (!report.prefabs.some((prefab) => prefab.path === "Assets/UnityAiE2E.prefab")) {
    fail("unity.prefabs.list did not include the e2e prefab fixture.");
  }
}

function assertPrefabInspect(report, expectedFound) {
  if (report.found !== expectedFound || typeof report.path !== "string" || typeof report.returnedGameObjectCount !== "number" || !Array.isArray(report.gameObjects)) {
    fail("unity.prefab.inspect returned an invalid prefab inspect shape.");
  }

  if (expectedFound) {
    if (report.rootName !== "UnityAiE2E") {
      fail(`unity.prefab.inspect expected rootName UnityAiE2E, got ${report.rootName}.`);
    }

    if (!report.gameObjects.some((gameObject) => Array.isArray(gameObject.components) && gameObject.components.includes("BoxCollider"))) {
      fail("unity.prefab.inspect did not include expected BoxCollider component.");
    }
  }
}

function assertAssetDependencies(report) {
  if (report.path !== "Packages/com.unity-ai.control-plane/package.json" || report.exists !== true || typeof report.totalFound !== "number" || typeof report.returned !== "number" || !Array.isArray(report.dependencies)) {
    fail("unity.asset.dependencies returned an invalid dependency report shape.");
  }

  if (report.returned > 50) {
    fail(`unity.asset.dependencies returned more dependencies than requested: ${report.returned}.`);
  }

  if (report.dependencies.some((dependency) => typeof dependency.path !== "string" || dependency.path.startsWith("/"))) {
    fail("unity.asset.dependencies returned an absolute dependency path.");
  }
}

function assertScriptList(report) {
  if (typeof report.totalFound !== "number" || typeof report.returned !== "number" || report.includePackages !== true || !Array.isArray(report.scripts)) {
    fail("unity.scripts.list returned an invalid script list shape.");
  }

  if (report.returned > 500) {
    fail(`unity.scripts.list returned more scripts than requested: ${report.returned}.`);
  }

  if (!report.scripts.some((script) => script.path === "Packages/com.unity-ai.control-plane/Editor/Bridge/UnityAiBridgeServer.cs")) {
    fail("unity.scripts.list did not include the Unity AI bridge script from the package.");
  }

  if (report.scripts.some((script) => typeof script.path !== "string" || script.path.startsWith("/"))) {
    fail("unity.scripts.list returned an absolute script path.");
  }
}

function assertAssemblyList(report) {
  if (typeof report.totalFound !== "number" || typeof report.returned !== "number" || !Array.isArray(report.assemblies) || report.assemblies.length === 0) {
    fail("unity.assemblies.list returned an invalid assembly list shape.");
  }

  if (!report.assemblies.some((assembly) => assembly.name === "Unity.AI.ControlPlane.Editor")) {
    fail("unity.assemblies.list did not include Unity.AI.ControlPlane.Editor.");
  }

  if (report.assemblies.some((assembly) => Object.prototype.hasOwnProperty.call(assembly, "assemblyPath") || Object.prototype.hasOwnProperty.call(assembly, "sourceFiles"))) {
    fail("unity.assemblies.list must not expose assemblyPath or sourceFiles.");
  }
}

function assertPackageList(report) {
  if (typeof report.totalFound !== "number" || !Array.isArray(report.packages) || report.packages.length === 0) {
    fail("unity.packages.list returned an invalid package list shape.");
  }

  if (report.packages.some((pkg) => Object.prototype.hasOwnProperty.call(pkg, "resolvedPath"))) {
    fail("unity.packages.list must not expose resolvedPath absolute paths.");
  }
}

function assertProjectSettings(report) {
  if (typeof report.unityVersion !== "undefined") {
    fail("unity.project.settings.inspect should not duplicate project inspect unityVersion yet.");
  }

  if (typeof report.activeBuildTarget !== "string" || typeof report.activeBuildTargetGroup !== "string" || typeof report.colorSpace !== "string") {
    fail("unity.project.settings.inspect returned an invalid settings shape.");
  }
}

function assertProjectSnapshot(snapshot) {
  if (!snapshot || typeof snapshot !== "object") {
    fail("unity.project.snapshot returned an invalid snapshot shape.");
  }

  if (!snapshot.identity || snapshot.identity.projectRoot !== "[project-root]" || snapshot.identity.dataPath !== "Assets" || typeof snapshot.identity.projectName !== "string" || typeof snapshot.identity.unityVersion !== "string" || Number.isNaN(Date.parse(snapshot.identity.capturedAtUtc))) {
    fail(`unity.project.snapshot returned invalid sanitized identity: ${JSON.stringify(snapshot.identity)}.`);
  }

  if (!snapshot.console || typeof snapshot.console.errorCount !== "number" || typeof snapshot.console.warningCount !== "number" || typeof snapshot.console.diagnosticCount !== "number" || !Array.isArray(snapshot.console.topDiagnostics)) {
    fail("unity.project.snapshot returned an invalid console summary.");
  }

  if (snapshot.console.topDiagnostics.length > 5) {
    fail(`unity.project.snapshot returned too many diagnostics: ${snapshot.console.topDiagnostics.length}.`);
  }

  if (snapshot.console.diagnosticCount === 0 || snapshot.console.errorCount === 0) {
    fail(`unity.project.snapshot did not reflect deterministic console diagnostics: ${JSON.stringify(snapshot.console)}.`);
  }

  if (!snapshot.scenes || typeof snapshot.scenes.totalFound !== "number" || typeof snapshot.scenes.buildSettingsCount !== "number" || !Array.isArray(snapshot.scenes.mainScenes)) {
    fail("unity.project.snapshot returned an invalid scene summary.");
  }

  if (snapshot.scenes.mainScenes.length > 10) {
    fail(`unity.project.snapshot returned too many scenes: ${snapshot.scenes.mainScenes.length}.`);
  }

  if (!snapshot.prefabs || typeof snapshot.prefabs.totalFound !== "number" || !Array.isArray(snapshot.prefabs.importantPrefabs) || snapshot.prefabs.importantPrefabs.length > 10) {
    fail("unity.project.snapshot returned an invalid bounded prefab summary.");
  }

  if (!snapshot.scripts || typeof snapshot.scripts.totalFound !== "number" || !Array.isArray(snapshot.scripts.importantScripts) || snapshot.scripts.importantScripts.length > 12) {
    fail("unity.project.snapshot returned an invalid bounded scripts summary.");
  }

  if (!snapshot.assemblies || typeof snapshot.assemblies.totalFound !== "number" || !Array.isArray(snapshot.assemblies.assemblies) || snapshot.assemblies.assemblies.length > 20) {
    fail("unity.project.snapshot returned an invalid bounded assemblies summary.");
  }

  if (!snapshot.packages || typeof snapshot.packages.totalFound !== "number" || !Array.isArray(snapshot.packages.packages) || snapshot.packages.packages.length > 20) {
    fail("unity.project.snapshot returned an invalid bounded packages summary.");
  }

  if (!snapshot.metaXr || typeof snapshot.metaXr.likelyMetaXrInstalled !== "boolean" || !Array.isArray(snapshot.metaXr.findings) || snapshot.metaXr.findings.length > 10) {
    fail("unity.project.snapshot returned an invalid Meta XR summary.");
  }

  if (!snapshot.artifacts || snapshot.artifacts.auditLogPath !== "UnityAIArtifacts/Audit/events.jsonl" || snapshot.artifacts.checkpointsPath !== "UnityAIArtifacts/Checkpoints" || typeof snapshot.artifacts.auditEventCount !== "number" || typeof snapshot.artifacts.checkpointCount !== "number") {
    fail("unity.project.snapshot returned an invalid sanitized artifact summary.");
  }

  if (!Array.isArray(snapshot.capabilities) || !snapshot.capabilities.some((capability) => capability.name === "unity.project.snapshot" && capability.effect === "read")) {
    fail("unity.project.snapshot did not include a useful capability summary.");
  }

  if (!Array.isArray(snapshot.riskFlags) || !snapshot.riskFlags.includes("compiler_errors_present") || !snapshot.riskFlags.includes("missing_bridge_token_not_relevant")) {
    fail(`unity.project.snapshot did not include expected risk flags: ${JSON.stringify(snapshot.riskFlags)}.`);
  }

  if (!Array.isArray(snapshot.recommendedNextActions) || !snapshot.recommendedNextActions.includes("run unity.console.diagnose")) {
    fail(`unity.project.snapshot did not include fact-based recommended actions: ${JSON.stringify(snapshot.recommendedNextActions)}.`);
  }

  if (!Array.isArray(snapshot.verificationSignals) || !snapshot.verificationSignals.includes("structured_observation") || !snapshot.verificationSignals.includes("console_diagnostics")) {
    fail(`unity.project.snapshot did not include expected verification signals: ${JSON.stringify(snapshot.verificationSignals)}.`);
  }

  assertNoAbsolutePathLeakInValue("snapshot", snapshot);
}

async function waitForConsoleDiagnostics(client, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let latest;

  while (Date.now() < deadline) {
    latest = await callJsonTool(client, "unity.console.diagnose", {});

    if (hasExpectedConsoleDiagnostics(latest)) {
      return latest;
    }

    await delay(1_000);
  }

  fail(`Timed out waiting for deterministic console diagnostics. Latest report: ${JSON.stringify(latest)}`);
}

function hasExpectedConsoleDiagnostics(report) {
  if (!report || !Array.isArray(report.diagnostics)) {
    return false;
  }

  return Boolean(
    findDiagnostic(report, diagnosticWarningMarker, "warning", "warning")
      && findDiagnostic(report, diagnosticErrorMarker, "error", "compiler_error")
      && findDiagnostic(report, diagnosticExceptionMarker, "error", "runtime_exception")
  );
}

function assertConsoleDiagnostics(report) {
  if (typeof report.timestampUtc !== "string" || Number.isNaN(Date.parse(report.timestampUtc))) {
    fail("unity.console.diagnose returned an invalid timestampUtc.");
  }

  if (typeof report.errorCount !== "number" || typeof report.warningCount !== "number" || typeof report.logCount !== "number" || typeof report.totalEntries !== "number" || typeof report.diagnosticCount !== "number" || typeof report.hasErrors !== "boolean" || !Array.isArray(report.diagnostics)) {
    fail("unity.console.diagnose returned an invalid diagnostic report shape.");
  }

  if (report.diagnosticCount !== report.diagnostics.length) {
    fail(`unity.console.diagnose diagnosticCount ${report.diagnosticCount} did not match diagnostics length ${report.diagnostics.length}.`);
  }

  if (!findDiagnostic(report, diagnosticWarningMarker, "warning", "warning")) {
    fail("unity.console.diagnose did not map the deterministic warning fixture to severity=warning category=warning.");
  }

  if (!findDiagnostic(report, diagnosticErrorMarker, "error", "compiler_error")) {
    fail("unity.console.diagnose did not map the deterministic error fixture to severity=error category=compiler_error.");
  }

  if (!findDiagnostic(report, diagnosticExceptionMarker, "error", "runtime_exception")) {
    fail("unity.console.diagnose did not map the deterministic exception fixture to severity=error category=runtime_exception.");
  }

  for (const diagnostic of report.diagnostics) {
    if (typeof diagnostic.category !== "string" || typeof diagnostic.severity !== "string" || typeof diagnostic.message !== "string" || typeof diagnostic.stackHint !== "string" || typeof diagnostic.functionHint !== "string" || typeof diagnostic.likelyRootCause !== "string" || typeof diagnostic.suggestedNextSafeAction !== "string") {
      fail("unity.console.diagnose returned a diagnostic with missing string fields.");
    }

    const diagnosticStringFields = ["category", "severity", "message", "file", "stackHint", "functionHint", "likelyRootCause", "suggestedNextSafeAction"];
    for (const field of diagnosticStringFields) {
      assertNoAbsolutePathLeak(`diagnostic.${field}`, diagnostic[field]);
    }

    if (typeof diagnostic.line !== "number") {
      fail("unity.console.diagnose returned a diagnostic with non-numeric line.");
    }
  }
}

function assertConsoleFixPlans(report) {
  if (typeof report.timestampUtc !== "string" || Number.isNaN(Date.parse(report.timestampUtc))) {
    fail("unity.console.plan_fix returned an invalid timestampUtc.");
  }

  if (typeof report.diagnosticCount !== "number" || typeof report.planCount !== "number" || !Array.isArray(report.plans)) {
    fail("unity.console.plan_fix returned an invalid fix plan report shape.");
  }

  if (report.planCount !== report.plans.length) {
    fail(`unity.console.plan_fix planCount ${report.planCount} did not match plans length ${report.plans.length}.`);
  }

  if (report.planCount === 0) {
    fail("unity.console.plan_fix returned no plans for deterministic console fixtures.");
  }

  if (!Array.isArray(report.verificationSignals) || !report.verificationSignals.includes("fix_plan_generated")) {
    fail("unity.console.plan_fix did not return fix_plan_generated verification signal.");
  }

  for (const category of ["compiler_error", "runtime_exception", "warning"]) {
    if (!report.plans.some((plan) => plan.diagnosticCategory === category)) {
      fail(`unity.console.plan_fix did not return a ${category} plan for deterministic fixtures.`);
    }
  }

  for (const [index, plan] of report.plans.entries()) {
    if (typeof plan.id !== "string" || !plan.id.length || typeof plan.diagnosticCategory !== "string" || typeof plan.severity !== "string" || typeof plan.targetFile !== "string" || typeof plan.summary !== "string" || typeof plan.rationale !== "string" || typeof plan.riskLevel !== "string" || typeof plan.rollbackNotes !== "string") {
      fail(`unity.console.plan_fix returned plan ${index} with missing string fields.`);
    }

    if (typeof plan.targetLine !== "number" || typeof plan.canAutoApply !== "boolean" || typeof plan.requiresConfirmationBeforeApply !== "boolean" || !Array.isArray(plan.proposedSteps) || !Array.isArray(plan.verificationSteps)) {
      fail(`unity.console.plan_fix returned plan ${index} with invalid typed fields.`);
    }

    if (plan.canAutoApply !== false) {
      fail(`unity.console.plan_fix plan ${plan.id} must not be auto-applicable in this read-only slice.`);
    }

    if (plan.requiresConfirmationBeforeApply !== true) {
      fail(`unity.console.plan_fix plan ${plan.id} must require confirmation before any later apply step.`);
    }

    for (const forbiddenField of ["auditEvents", "auditPersisted", "auditLogPath", "auditLogAbsolutePath", "created", "undone", "mutated", "rootGameObjectCountAfter"]) {
      if (Object.prototype.hasOwnProperty.call(plan, forbiddenField)) {
        fail(`unity.console.plan_fix plan ${plan.id} exposed mutation indicator field ${forbiddenField}.`);
      }
    }

    assertNoAbsolutePathLeakInValue(`plan.${plan.id}`, plan);
  }
}

function findDiagnostic(report, marker, severity, category) {
  return report.diagnostics.find((diagnostic) => diagnostic.message.includes(marker) && diagnostic.severity === severity && diagnostic.category === category);
}

function assertNoAbsolutePathLeak(field, value) {
  if (typeof value !== "string" || value.length === 0) {
    return;
  }

  const normalized = value.replaceAll("\\", "/");
  const normalizedTempProject = tempProject.replaceAll("\\", "/");
  const normalizedRepoRoot = repoRoot.replaceAll("\\", "/");

  if (normalized.includes(normalizedTempProject) || normalized.includes(normalizedRepoRoot)) {
    fail(`unity.console.diagnose leaked the absolute project root in ${field}: ${value}`);
  }

  if (/\b[A-Za-z]:\//.test(normalized)) {
    fail(`unity.console.diagnose leaked a Windows absolute path in ${field}: ${value}`);
  }

  if (/(^|[\s"'(])\/Users\//.test(normalized)) {
    fail(`unity.console.diagnose leaked a /Users absolute path in ${field}: ${value}`);
  }

  if (/(^|[\s"'(])\/(?!absolute-path\])[^\s"'()]+/.test(normalized)) {
    fail(`unity.console.diagnose leaked a generic Unix absolute path in ${field}: ${value}`);
  }
}

function assertNoAbsolutePathLeakInValue(field, value) {
  if (typeof value === "string") {
    assertNoAbsolutePathLeak(field, value);
    return;
  }

  if (Array.isArray(value)) {
    value.forEach((item, index) => assertNoAbsolutePathLeakInValue(`${field}[${index}]`, item));
    return;
  }

  if (value && typeof value === "object") {
    for (const [key, nestedValue] of Object.entries(value)) {
      assertNoAbsolutePathLeakInValue(`${field}.${key}`, nestedValue);
    }
  }
}

function assertOperationCommon(label, result, expected) {
  if (result.rootGameObjectCountBefore !== expected.beforeCount) {
    fail(`${label}: expected before count ${expected.beforeCount}, got ${result.rootGameObjectCountBefore}.`);
  }

  if (result.rootGameObjectCountAfter !== expected.afterCount) {
    fail(`${label}: expected after count ${expected.afterCount}, got ${result.rootGameObjectCountAfter}.`);
  }

  if (typeof result.audit !== "string" || result.audit.length === 0) {
    fail(`${label}: expected non-empty audit string.`);
  }

  if (typeof result.verification !== "string" || result.verification.length === 0) {
    fail(`${label}: expected non-empty verification string.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1) {
    fail(`${label}: expected exactly one structured audit event.`);
  }

  const [auditEvent] = result.auditEvents;

  if (auditEvent.capability !== expected.capability) {
    fail(`${label}: expected audit event capability ${expected.capability}, got ${auditEvent.capability}.`);
  }

  if (JSON.stringify(auditEvent.effects) !== JSON.stringify(expected.effects)) {
    fail(`${label}: expected audit effects ${JSON.stringify(expected.effects)}, got ${JSON.stringify(auditEvent.effects)}.`);
  }

  if (Number.isNaN(Date.parse(auditEvent.timestamp))) {
    fail(`${label}: expected audit timestamp to be ISO-parseable, got ${auditEvent.timestamp}.`);
  }

  if (Number.isNaN(Date.parse(result.timestampUtc))) {
    fail(`${label}: expected result timestampUtc to be ISO-parseable, got ${result.timestampUtc}.`);
  }

  const expectedVerificationStatus = expected.verificationStatus ?? "passed";
  if (result.verificationStatus !== expectedVerificationStatus) {
    fail(`${label}: expected verificationStatus ${expectedVerificationStatus}, got ${result.verificationStatus}.`);
  }

  const expectedRequiresConfirmation = expected.requiresConfirmation ?? false;
  if (result.requiresConfirmation !== expectedRequiresConfirmation) {
    fail(`${label}: expected requiresConfirmation ${expectedRequiresConfirmation}, got ${result.requiresConfirmation}.`);
  }

  if (!Array.isArray(result.requiredPermissions) || !result.requiredPermissions.includes("modify_scenes")) {
    fail(`${label}: expected requiredPermissions to include modify_scenes.`);
  }

  if (result.auditPersisted !== true) {
    fail(`${label}: expected auditPersisted=true, got ${result.auditPersisted}.`);
  }

  if (typeof result.auditLogPath !== "string" || result.auditLogPath.length === 0 || result.auditLogPath.startsWith("/")) {
    fail(`${label}: expected auditLogPath to be project-relative, got ${result.auditLogPath}.`);
  }

  if (typeof result.auditLogAbsolutePath !== "string" || result.auditLogAbsolutePath.length === 0 || !existsSync(result.auditLogAbsolutePath)) {
    fail(`${label}: expected auditLogAbsolutePath to point to an existing file, got ${result.auditLogAbsolutePath}.`);
  }

  assertAuditLogContains(label, result.auditLogAbsolutePath, auditEvent);

  if (!Array.isArray(result.verificationSignals)) {
    fail(`${label}: expected verificationSignals array.`);
  }

  for (const signal of expected.requiredSignals) {
    if (!result.verificationSignals.includes(signal)) {
      fail(`${label}: expected verificationSignals to include ${signal}.`);
    }
  }

  for (const signal of expected.forbiddenSignals) {
    if (result.verificationSignals.includes(signal)) {
      fail(`${label}: did not expect verificationSignals to include ${signal}.`);
    }
  }
}

function assertAuditLogContains(label, auditLogPath, expectedEvent) {
  const lines = readFileSync(auditLogPath, "utf8")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);

  const found = lines.some((line) => {
    try {
      const parsed = JSON.parse(line);
      return parsed.timestamp === expectedEvent.timestamp
        && parsed.capability === expectedEvent.capability
        && parsed.requestId === expectedEvent.requestId
        && parsed.correlationId === expectedEvent.correlationId
        && parsed.message === expectedEvent.message
        && JSON.stringify(parsed.effects) === JSON.stringify(expectedEvent.effects);
    } catch {
      return false;
    }
  });

  if (!found) {
    fail(`${label}: expected persisted audit log to contain the returned audit event.`);
  }
}

async function waitForFile(path, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (existsSync(path)) {
      return;
    }

    await delay(500);
  }

  fail(`Timed out waiting for file: ${path}`);
}

async function waitForBridgeHealth(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${url}/health`);

      if (response.ok) {
        return;
      }
    } catch {
      // Keep polling until Unity starts the local bridge listener.
    }

    await delay(500);
  }

  fail(`Timed out waiting for Unity bridge health at ${url}`);
}

async function waitForBridgeCapability(url, capability, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${url}/capabilities/${encodeURIComponent(capability)}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ input: {} })
      });

      if (response.ok) {
        return;
      }
    } catch {
      // Keep polling until Unity can process main-thread capability work.
    }

    await delay(1_000);
  }

  fail(`Timed out waiting for Unity bridge capability ${capability} at ${url}`);
}

async function assertMutatingRouteRequiresToken(url) {
  const response = await fetch(`${url}/capabilities/${encodeURIComponent("unity.editor.create_empty_game_object")}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ input: { name: "Unauthorized E2E Object", dryRun: false } })
  });

  if (response.status !== 403) {
    fail(`Expected mutating route without token to return 403, got HTTP ${response.status}.`);
  }

  console.log("✓ unity.editor.create_empty_game_object rejects missing token");

  const applyFixResponse = await fetch(`${url}/capabilities/${encodeURIComponent("unity.console.apply_fix")}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ input: { targetFile: applyFixFile, targetLine: 1, expectedOriginalLine: "x", replacementLine: "y" } })
  });

  if (applyFixResponse.status !== 403) {
    fail(`Expected unity.console.apply_fix without token to return 403, got HTTP ${applyFixResponse.status}.`);
  }

  console.log("✓ unity.console.apply_fix rejects missing token");
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function waitForProcessExit(process, timeoutMs) {
  return new Promise((resolve) => {
    const timeout = setTimeout(resolve, timeoutMs);
    process.once("exit", () => {
      clearTimeout(timeout);
      resolve();
    });
  });
}

function fail(message) {
  console.error(message);
  try {
    if (clean) {
      rmSync(tempProject, { recursive: true, force: true });
    }
  } catch {
    // ignore cleanup errors
  }
  process.exit(1);
}
