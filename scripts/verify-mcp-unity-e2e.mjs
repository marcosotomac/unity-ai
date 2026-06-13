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
mkdirSync(join(assetsDir, "Tests/EditMode"), { recursive: true });
mkdirSync(join(assetsDir, "Tests/PlayMode"), { recursive: true });
mkdirSync(projectSettingsDir, { recursive: true });
mkdirSync(artifactsDir, { recursive: true });
rmSync(join(tempProject, "Library/UnityAIControlPlane"), { recursive: true, force: true });
rmSync(join(tempProject, "UnityAIArtifacts"), { recursive: true, force: true });
rmSync(join(assetsDir, "UnityAiGenerated"), { recursive: true, force: true });
rmSync(join(assetsDir, "UnityAiGenerated.meta"), { force: true });
rmSync(join(assetsDir, "UnityAiE2E.unity"), { force: true });
rmSync(join(assetsDir, "UnityAiE2E.unity.meta"), { force: true });
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
        "com.unity-ai.control-plane": "file:com.unity-ai.control-plane",
        "com.unity.test-framework": "1.6.0"
      }
    },
    null,
    2
  )
);

writeFileSync(join(projectSettingsDir, "ProjectVersion.txt"), "m_EditorVersion: 6000.4.9f1\n");
writeFileSync(join(assetsDir, "UnityAiCheckpointFixture.txt"), "checkpoint-before\n");
writeFileSync(
  join(assetsDir, "Tests/EditMode/UnityAiE2E.EditMode.asmdef"),
  JSON.stringify({
    name: "UnityAiE2E.EditMode",
    includePlatforms: ["Editor"],
    optionalUnityReferences: ["TestAssemblies"]
  }, null, 2)
);
writeFileSync(
  join(assetsDir, "Tests/EditMode/UnityAiEditModeTests.cs"),
  `using NUnit.Framework;

public sealed class UnityAiEditModeTests
{
    [Test]
    public void PersistentEditModeJobRuns()
    {
        Assert.That(2 + 2, Is.EqualTo(4));
    }
}
`
);
writeFileSync(
  join(assetsDir, "Tests/PlayMode/UnityAiE2E.PlayMode.asmdef"),
  JSON.stringify({
    name: "UnityAiE2E.PlayMode",
    optionalUnityReferences: ["TestAssemblies"]
  }, null, 2)
);
writeFileSync(
  join(assetsDir, "Tests/PlayMode/UnityAiPlayModeTests.cs"),
  `using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public sealed class UnityAiPlayModeTests
{
    [UnityTest]
    public IEnumerator PersistentPlayModeJobRuns()
    {
        yield return null;
        Assert.Pass();
    }
}
`
);
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
  await waitForFile(readyFile, 180_000);
  await waitForBridgeHealth(bridgeUrl, 180_000);
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
  await assertVisualVerificationFlow(client);
  await assertExtendedControlPlaneFlow(client);

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

  await assertSceneUpsertGameObjectFlow(client);
  await assertSceneBatchFlow(client);

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

  const sceneUpsertCapability = capabilities.find((capability) => capability.name === "unity.scene.upsert_game_object");
  if (!sceneUpsertCapability) {
    fail("unity.capabilities.list did not include unity.scene.upsert_game_object.");
  }

  if (!Array.isArray(sceneUpsertCapability.permissions) || !sceneUpsertCapability.permissions.includes("modify_scenes")) {
    fail("unity.scene.upsert_game_object capability must declare modify_scenes permission.");
  }

  if (!Array.isArray(sceneUpsertCapability.effects) || !sceneUpsertCapability.effects.includes("write_audit_log") || !sceneUpsertCapability.effects.includes("scene_change")) {
    fail("unity.scene.upsert_game_object capability must declare audit and scene change effects.");
  }

  if (!Array.isArray(sceneUpsertCapability.verification) || !sceneUpsertCapability.verification.includes("scene_mutation_verified")) {
    fail("unity.scene.upsert_game_object capability must declare scene_mutation_verified verification.");
  }

  const gameObjectInspectCapability = capabilities.find((capability) => capability.name === "unity.scene.inspect_game_object");
  if (!gameObjectInspectCapability || !gameObjectInspectCapability.permissions.includes("read_scenes") || !gameObjectInspectCapability.effects.includes("report_only")) {
    fail("unity.scene.inspect_game_object must be a read-only scene inspection capability.");
  }

  const sceneBatchCapability = capabilities.find((capability) => capability.name === "unity.scene.batch");
  if (!sceneBatchCapability) {
    fail("unity.capabilities.list did not include unity.scene.batch.");
  }

  if (!sceneBatchCapability.permissions.includes("modify_scenes") || !sceneBatchCapability.permissions.includes("read_assets")) {
    fail("unity.scene.batch must declare modify_scenes and read_assets permissions.");
  }

  if (!sceneBatchCapability.effects.includes("write_audit_log") || !sceneBatchCapability.effects.includes("scene_change")) {
    fail("unity.scene.batch must declare audit and scene change effects.");
  }

  for (const signal of ["scene_mutation_verified", "batch_applied", "component_state_verified"]) {
    if (!sceneBatchCapability.verification.includes(signal)) {
      fail(`unity.scene.batch must declare ${signal} verification.`);
    }
  }

  const captureCapability = capabilities.find((capability) => capability.name === "unity.vision.capture");
  if (!captureCapability || !captureCapability.permissions.includes("capture_screenshots") || !captureCapability.permissions.includes("write_artifacts")) {
    fail("unity.vision.capture must declare screenshot capture and artifact permissions.");
  }

  if (!captureCapability.verification.includes("screenshot_available") || !captureCapability.verification.includes("screenshot_ready")) {
    fail("unity.vision.capture must declare screenshot availability and readiness verification.");
  }

  const compareCapability = capabilities.find((capability) => capability.name === "unity.vision.compare");
  if (!compareCapability || !compareCapability.permissions.includes("read_artifacts") || !compareCapability.permissions.includes("write_artifacts")) {
    fail("unity.vision.compare must declare read_artifacts and write_artifacts permissions.");
  }

  if (!compareCapability.verification.includes("visual_diff_checked") || !compareCapability.verification.includes("visual_regression_detected") || !compareCapability.verification.includes("visual_regression_absent")) {
    fail("unity.vision.compare must declare diff and regression verification signals.");
  }

  for (const name of [
    "unity.tests.run",
    "unity.playmode.control",
    "unity.compilation.wait",
    "unity.build.android",
    "unity.assets.author",
    "unity.prefab.manage",
    "unity.checkpoints.restore",
    "unity.project.settings.update",
    "unity.packages.change",
    "unity.meta_xr.configure"
  ]) {
    if (!capabilities.some((capability) => capability.name === name)) {
      fail(`unity.capabilities.list did not include ${name}.`);
    }
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

function assertSceneUpsertResult(label, result, expected) {
  if (result.dryRun !== expected.dryRun || result.created !== expected.created || result.updated !== expected.updated) {
    fail(`${label}: expected dryRun=${expected.dryRun} created=${expected.created} updated=${expected.updated}, got dryRun=${result.dryRun} created=${result.created} updated=${result.updated}.`);
  }

  if (result.refused === true) {
    fail(`${label}: did not expect refusal.`);
  }

  if (result.rootGameObjectCountBefore !== expected.beforeCount) {
    fail(`${label}: expected before count ${expected.beforeCount}, got ${result.rootGameObjectCountBefore}.`);
  }

  if (result.rootGameObjectCountAfter !== expected.afterCount) {
    fail(`${label}: expected after count ${expected.afterCount}, got ${result.rootGameObjectCountAfter}.`);
  }

  if (typeof result.audit !== "string" || result.audit.length === 0 || typeof result.verification !== "string" || result.verification.length === 0) {
    fail(`${label}: expected non-empty audit and verification strings.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1) {
    fail(`${label}: expected exactly one structured audit event.`);
  }

  const [auditEvent] = result.auditEvents;
  if (auditEvent.capability !== "unity.scene.upsert_game_object") {
    fail(`${label}: expected audit event capability unity.scene.upsert_game_object, got ${auditEvent.capability}.`);
  }

  if (JSON.stringify(auditEvent.effects) !== JSON.stringify(expected.effects)) {
    fail(`${label}: expected audit effects ${JSON.stringify(expected.effects)}, got ${JSON.stringify(auditEvent.effects)}.`);
  }

  if (typeof result.requestId !== "string" || result.requestId.length === 0 || typeof result.correlationId !== "string" || result.correlationId.length === 0) {
    fail(`${label}: expected non-empty requestId and correlationId.`);
  }

  if (auditEvent.requestId !== result.requestId || auditEvent.correlationId !== result.correlationId) {
    fail(`${label}: expected audit request/correlation ids to match result.`);
  }

  if (Number.isNaN(Date.parse(auditEvent.timestamp)) || Number.isNaN(Date.parse(result.timestampUtc))) {
    fail(`${label}: expected ISO-parseable timestamps.`);
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

  if (result.auditPersisted !== true || typeof result.auditLogPath !== "string" || result.auditLogPath.startsWith("/")) {
    fail(`${label}: expected persisted relative audit log path.`);
  }

  assertAuditLogContains(label, join(tempProject, result.auditLogPath), auditEvent);

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

  if (Object.prototype.hasOwnProperty.call(result, "auditLogAbsolutePath")) {
    fail(`${label}: scene upsert must not expose auditLogAbsolutePath.`);
  }

  assertNoAbsolutePathLeakInValue(label, result);
}

function findSceneObject(report, path) {
  if (!report || !Array.isArray(report.gameObjects)) {
    return undefined;
  }

  return report.gameObjects.find((gameObject) => gameObject.path === path);
}

function assertVectorClose(label, actual, expected) {
  if (!actual || typeof actual.x !== "number" || typeof actual.y !== "number" || typeof actual.z !== "number") {
    fail(`${label}: expected vector fields, got ${JSON.stringify(actual)}.`);
  }

  for (const axis of ["x", "y", "z"]) {
    if (Math.abs(actual[axis] - expected[axis]) > 0.001) {
      fail(`${label}: expected ${axis}=${expected[axis]}, got ${actual[axis]}.`);
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

async function assertSceneUpsertGameObjectFlow(client) {
  const before = await callJsonTool(client, "unity.project.inspect", {});

  const dryRun = await callJsonTool(client, "unity.scene.upsert_game_object", {
    name: "UnityAiE2EDryRunCube",
    path: "UnityAiAuthoringDryRun",
    primitive: "cube"
  });
  assertSceneUpsertResult("dry-run scene upsert", dryRun, {
    dryRun: true,
    created: false,
    updated: false,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["scene_mutation_verified"]
  });

  let inspected = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 5, maxGameObjects: 100 });
  if (findSceneObject(inspected, "UnityAiAuthoringDryRun/UnityAiE2EDryRunCube")) {
    fail("dry-run scene upsert created an object.");
  }

  const needsConfirmation = await callJsonTool(client, "unity.scene.upsert_game_object", {
    name: "UnityAiE2ENeedsConfirmation",
    path: "UnityAiAuthoringNeedsConfirmation",
    primitive: "cube",
    dryRun: false,
    confirm: false
  });
  assertSceneUpsertResult("scene upsert confirmation gate", needsConfirmation, {
    dryRun: false,
    created: false,
    updated: false,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["scene_mutation_verified"],
    verificationStatus: "needs_confirmation",
    requiresConfirmation: true
  });

  const created = await callJsonTool(client, "unity.scene.upsert_game_object", {
    name: "UnityAiE2ECube",
    path: "UnityAiAuthoring",
    primitive: "cube",
    transform: {
      position: { x: 1.5, y: 2.25, z: -3.5 },
      rotationEuler: { x: 0, y: 45, z: 0 },
      scale: { x: 2, y: 3, z: 4 }
    },
    tag: "Untagged",
    layer: "Default",
    active: true,
    dryRun: false,
    confirm: true
  });
  assertSceneUpsertResult("real scene upsert create", created, {
    dryRun: false,
    created: true,
    updated: false,
    beforeCount: before.rootGameObjectCount,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "scene_mutation_verified"],
    forbiddenSignals: [],
    verificationStatus: "passed"
  });

  inspected = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 5, maxGameObjects: 100 });
  const cube = findSceneObject(inspected, "UnityAiAuthoring/UnityAiE2ECube");
  if (!cube) {
    fail("scene inspect did not include created upsert cube.");
  }

  if (!Array.isArray(cube.components) || !cube.components.includes("BoxCollider")) {
    fail(`created upsert cube did not include expected primitive BoxCollider: ${JSON.stringify(cube.components)}.`);
  }

  assertVectorClose("created cube position", cube.position, { x: 1.5, y: 2.25, z: -3.5 });
  assertVectorClose("created cube scale", cube.scale, { x: 2, y: 3, z: 4 });

  const updated = await callJsonTool(client, "unity.scene.upsert_game_object", {
    name: "UnityAiE2ECube",
    path: "UnityAiAuthoring",
    mode: "update",
    transform: {
      position: { x: -4, y: 0.5, z: 8 }
    },
    active: false,
    dryRun: false,
    confirm: true
  });
  assertSceneUpsertResult("real scene upsert update", updated, {
    dryRun: false,
    created: false,
    updated: true,
    beforeCount: before.rootGameObjectCount + 1,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "scene_mutation_verified"],
    forbiddenSignals: [],
    verificationStatus: "passed"
  });

  inspected = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 5, maxGameObjects: 100 });
  const updatedCube = findSceneObject(inspected, "UnityAiAuthoring/UnityAiE2ECube");
  if (!updatedCube || updatedCube.activeSelf !== false) {
    fail("scene upsert update did not set active=false.");
  }
  assertVectorClose("updated cube position", updatedCube.position, { x: -4, y: 0.5, z: 8 });

  await assertSceneUpsertRefused(client, "bad name", { name: "Bad/Name" });
  await assertSceneUpsertRefused(client, "bad path", { name: "BadPath", path: "Environment//Rock" });
  await assertSceneUpsertRefused(client, "extreme transform", { name: "BadTransform", transform: { position: { x: 1000000, y: 0, z: 0 } } });
  await assertSceneUpsertRefused(client, "invalid tag", { name: "BadTag", tag: "UnityAiTagThatMustNotExist" });
  await assertSceneUpsertRefused(client, "invalid layer", { name: "BadLayer", layer: "UnityAiLayerThatMustNotExist" });

  const undoUpdate = await callJsonTool(client, "unity.editor.undo_last_operation", {
    dryRun: false,
    confirm: true
  });
  assertUndoResult("undo scene upsert update", undoUpdate, {
    dryRun: false,
    undone: false,
    beforeCount: before.rootGameObjectCount + 1,
    afterCount: before.rootGameObjectCount + 1,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: [],
    verificationStatus: "failed",
    requiresConfirmation: false
  });

  inspected = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 5, maxGameObjects: 100 });
  const cubeAfterUndo = findSceneObject(inspected, "UnityAiAuthoring/UnityAiE2ECube");
  if (!cubeAfterUndo || cubeAfterUndo.activeSelf !== true) {
    fail("undo of scene upsert update did not restore active=true.");
  }

  assertNoAbsolutePathLeakInValue("scene upsert flow", [dryRun, needsConfirmation, created, updated]);
}

async function assertSceneUpsertRefused(client, label, input) {
  const result = await callJsonTool(client, "unity.scene.upsert_game_object", input);
  if (result.refused !== true || result.created !== false || result.updated !== false || result.verificationStatus !== "refused") {
    fail(`${label}: expected scene upsert refusal, got ${JSON.stringify(result)}.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1 || result.auditEvents[0].capability !== "unity.scene.upsert_game_object") {
    fail(`${label}: refused scene upsert must return one structured audit event.`);
  }

  assertNoAbsolutePathLeakInValue(label, result);
}

async function assertSceneBatchFlow(client) {
  const dryRun = await callJsonTool(client, "unity.scene.batch", {
    operations: [
      { kind: "create", name: "UnityAiBatchDryRun", primitive: "empty" }
    ]
  });
  assertSceneBatchResult("dry-run scene batch", dryRun, {
    dryRun: true,
    applied: false,
    rolledBack: false,
    verificationStatus: "passed",
    requiresConfirmation: false,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["batch_applied", "component_state_verified", "scene_mutation_verified"]
  });

  let scene = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 6, maxGameObjects: 200 });
  if (findSceneObject(scene, "UnityAiBatchDryRun")) {
    fail("dry-run scene batch created a GameObject.");
  }

  const needsConfirmation = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    operations: [
      { kind: "create", name: "UnityAiBatchNeedsConfirmation", primitive: "empty" }
    ]
  });
  assertSceneBatchResult("scene batch confirmation gate", needsConfirmation, {
    dryRun: false,
    applied: false,
    rolledBack: false,
    verificationStatus: "needs_confirmation",
    requiresConfirmation: true,
    effects: ["report_only", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["batch_applied", "component_state_verified", "scene_mutation_verified"]
  });

  const applied = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      { kind: "create", name: "UnityAiBatchRoot", primitive: "empty" },
      {
        kind: "reparent",
        targetPath: "UnityAiAuthoring/UnityAiE2ECube",
        parentPath: "UnityAiBatchRoot",
        worldPositionStays: true
      },
      {
        kind: "add_component",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        componentType: "UnityEngine.Rigidbody"
      },
      {
        kind: "set_property",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        componentType: "UnityEngine.Rigidbody",
        propertyPath: "m_Mass",
        value: { kind: "number", numberValue: 2.5 }
      },
      {
        kind: "set_property",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        componentType: "UnityEngine.Rigidbody",
        propertyPath: "m_UseGravity",
        value: { kind: "bool", boolValue: false }
      },
      {
        kind: "duplicate",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        name: "UnityAiBatchCopy",
        worldPositionStays: true
      },
      {
        kind: "rename",
        targetPath: "UnityAiBatchRoot/UnityAiBatchCopy",
        name: "UnityAiBatchRenamed"
      },
      {
        kind: "set_active",
        targetPath: "UnityAiBatchRoot/UnityAiBatchRenamed",
        active: false
      },
      {
        kind: "instantiate_prefab",
        prefabPath: "Assets/UnityAiE2E.prefab",
        parentPath: "UnityAiBatchRoot",
        name: "UnityAiBatchPrefab"
      }
    ]
  });
  assertSceneBatchResult("real scene batch", applied, {
    dryRun: false,
    applied: true,
    rolledBack: false,
    verificationStatus: "passed",
    requiresConfirmation: false,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation", "batch_applied", "component_state_verified", "scene_mutation_verified"],
    forbiddenSignals: []
  });

  if (applied.appliedOperationCount !== 9 || !Array.isArray(applied.operations) || applied.operations.some((operation) => operation.applied !== true || operation.verified !== true)) {
    fail(`real scene batch did not verify all operations: ${JSON.stringify(applied.operations)}.`);
  }

  const inspected = await callJsonTool(client, "unity.scene.inspect_game_object", {
    path: "UnityAiBatchRoot/UnityAiE2ECube",
    includeProperties: true,
    maxProperties: 1000,
    maxPropertyDepth: 8
  });
  assertGameObjectInspection(inspected);

  scene = await callJsonTool(client, "unity.scene.inspect", { includeComponents: true, maxDepth: 6, maxGameObjects: 200 });
  const renamedCopy = findSceneObject(scene, "UnityAiBatchRoot/UnityAiBatchRenamed");
  if (!renamedCopy || renamedCopy.activeSelf !== false) {
    fail("scene batch did not create, rename, and deactivate the duplicate.");
  }

  if (!findSceneObject(scene, "UnityAiBatchRoot/UnityAiBatchPrefab")) {
    fail("scene batch did not instantiate the prefab.");
  }

  const rolledBack = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      {
        kind: "set_active",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        active: false
      },
      {
        kind: "add_component",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        componentType: "UnityAi.Type.That.Does.Not.Exist"
      }
    ]
  });
  assertSceneBatchResult("failed scene batch rollback", rolledBack, {
    dryRun: false,
    applied: false,
    rolledBack: true,
    verificationStatus: "failed",
    requiresConfirmation: false,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["operation_audited", "structured_observation"],
    forbiddenSignals: ["batch_applied", "component_state_verified", "scene_mutation_verified"]
  });

  const afterRollback = await callJsonTool(client, "unity.scene.inspect_game_object", {
    path: "UnityAiBatchRoot/UnityAiE2ECube",
    includeProperties: false
  });
  if (afterRollback.activeSelf !== true) {
    fail("failed scene batch did not roll back the earlier active-state mutation.");
  }

  const cleanup = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      {
        kind: "remove_component",
        targetPath: "UnityAiBatchRoot/UnityAiE2ECube",
        componentType: "UnityEngine.Rigidbody"
      },
      { kind: "delete", targetPath: "UnityAiBatchRoot/UnityAiBatchRenamed" },
      { kind: "delete", targetPath: "UnityAiBatchRoot/UnityAiBatchPrefab" }
    ]
  });
  assertSceneBatchResult("scene batch cleanup", cleanup, {
    dryRun: false,
    applied: true,
    rolledBack: false,
    verificationStatus: "passed",
    requiresConfirmation: false,
    effects: ["scene_change", "write_audit_log"],
    requiredSignals: ["batch_applied", "component_state_verified", "scene_mutation_verified"],
    forbiddenSignals: []
  });
}

function assertSceneBatchResult(label, result, expected) {
  if (result.dryRun !== expected.dryRun || result.applied !== expected.applied || result.rolledBack !== expected.rolledBack) {
    fail(`${label}: unexpected batch state ${JSON.stringify({ dryRun: result.dryRun, applied: result.applied, rolledBack: result.rolledBack })}.`);
  }

  if (result.verificationStatus !== expected.verificationStatus || result.requiresConfirmation !== expected.requiresConfirmation) {
    fail(`${label}: unexpected verification state ${result.verificationStatus}, confirmation=${result.requiresConfirmation}.`);
  }

  if (!Array.isArray(result.auditEvents) || result.auditEvents.length !== 1 || result.auditEvents[0].capability !== "unity.scene.batch") {
    fail(`${label}: expected one unity.scene.batch audit event.`);
  }

  const [auditEvent] = result.auditEvents;
  if (JSON.stringify(auditEvent.effects) !== JSON.stringify(expected.effects)) {
    fail(`${label}: expected audit effects ${JSON.stringify(expected.effects)}, got ${JSON.stringify(auditEvent.effects)}.`);
  }

  if (auditEvent.requestId !== result.requestId || auditEvent.correlationId !== result.correlationId) {
    fail(`${label}: audit correlation identifiers do not match.`);
  }

  if (!Array.isArray(result.requiredPermissions) || !result.requiredPermissions.includes("modify_scenes") || !result.requiredPermissions.includes("read_assets")) {
    fail(`${label}: expected read_assets and modify_scenes permissions.`);
  }

  if (result.auditPersisted !== true || typeof result.auditLogPath !== "string" || result.auditLogPath.startsWith("/")) {
    fail(`${label}: expected a persisted project-relative audit log.`);
  }

  assertAuditLogContains(label, join(tempProject, result.auditLogPath), auditEvent);

  for (const signal of expected.requiredSignals) {
    if (!result.verificationSignals.includes(signal)) {
      fail(`${label}: expected verification signal ${signal}.`);
    }
  }

  for (const signal of expected.forbiddenSignals) {
    if (result.verificationSignals.includes(signal)) {
      fail(`${label}: did not expect verification signal ${signal}.`);
    }
  }

  assertNoAbsolutePathLeakInValue(label, result);
}

function assertGameObjectInspection(report) {
  if (report.found !== true || report.path !== "UnityAiBatchRoot/UnityAiE2ECube" || !Array.isArray(report.components)) {
    fail(`unity.scene.inspect_game_object returned an invalid report: ${JSON.stringify(report)}.`);
  }

  const rigidbody = report.components.find((component) => component.fullTypeName === "UnityEngine.Rigidbody");
  if (!rigidbody || !Array.isArray(rigidbody.properties)) {
    fail("unity.scene.inspect_game_object did not include the Rigidbody component.");
  }

  if (rigidbody.componentTypeIndex !== 0 || typeof rigidbody.index !== "number") {
    fail("unity.scene.inspect_game_object did not expose stable component indices.");
  }

  const mass = rigidbody.properties.find((property) => property.path === "m_Mass");
  const useGravity = rigidbody.properties.find((property) => property.path === "m_UseGravity");
  if (!mass || Math.abs(Number(mass.value) - 2.5) > 0.001 || !useGravity || useGravity.value !== "false") {
    fail(`unity.scene.inspect_game_object did not verify Rigidbody properties: ${JSON.stringify(rigidbody.properties)}.`);
  }

  assertNoAbsolutePathLeakInValue("game object inspection", report);
}

async function assertVisualVerificationFlow(client) {
  const refusedAbsolutePath = await callJsonTool(client, "unity.vision.compare", {
    beforePath: "/tmp/unity-ai-before.png",
    afterPath: "/tmp/unity-ai-after.png"
  });
  if (refusedAbsolutePath.compared !== false || refusedAbsolutePath.beforePath !== "" || refusedAbsolutePath.afterPath !== "") {
    fail(`visual comparison did not safely refuse absolute artifact paths: ${JSON.stringify(refusedAbsolutePath)}.`);
  }
  assertNoAbsolutePathLeakInValue("visual absolute path refusal", refusedAbsolutePath);

  const baseline = await callJsonTool(client, "unity.vision.capture", {
    source: "game",
    width: 160,
    height: 90,
    label: "baseline",
    cameraPath: "UnityAiE2EVisualCamera"
  });
  assertReadyScreenshot("visual baseline", baseline, 160, 90);

  const identical = await callJsonTool(client, "unity.vision.compare", {
    beforePath: baseline.path,
    afterPath: baseline.path,
    pixelThreshold: 0.01,
    maxChangedPixelRatio: 0,
    maxMeanAbsoluteError: 0,
    generateDiff: true,
    label: "identical"
  });
  assertVisualComparison("identical visual comparison", identical, false);

  const regression = await callJsonTool(client, "unity.vision.compare", {
    beforePath: "UnityAIArtifacts/Screenshots/e2e-before.png",
    afterPath: "UnityAIArtifacts/Screenshots/e2e-after.png",
    pixelThreshold: 0.05,
    maxChangedPixelRatio: 0.01,
    maxMeanAbsoluteError: 0.01,
    generateDiff: true,
    label: "regression"
  });
  assertVisualComparison("visual regression comparison", regression, true);

  if (regression.changedPixelRatio < 0.99 || regression.meanAbsoluteError <= 0.1) {
    fail(`visual regression metrics were unexpectedly weak: ${JSON.stringify(regression)}.`);
  }
}

async function assertExtendedControlPlaneFlow(client) {
  const settingsPreview = await callJsonTool(client, "unity.project.settings.update", {
    productName: "Unity AI E2E Preview"
  });
  if (settingsPreview.dryRun !== true || !settingsPreview.changedFields?.includes("productName")) {
    fail(`project settings dry run was invalid: ${JSON.stringify(settingsPreview)}.`);
  }

  const packagePreview = await callJsonTool(client, "unity.packages.change", {
    add: ["com.unity.xr.management"]
  });
  if (packagePreview.dryRun !== true || packagePreview.status !== "preview") {
    fail(`package change dry run was invalid: ${JSON.stringify(packagePreview)}.`);
  }

  const metaPreview = await callJsonTool(client, "unity.meta_xr.configure", {});
  if (metaPreview.dryRun !== true || metaPreview.status !== "preview") {
    fail(`Meta XR configuration dry run was invalid: ${JSON.stringify(metaPreview)}.`);
  }

  const buildValidation = await callJsonTool(client, "unity.build.validate_android_quest", {});
  if (typeof buildValidation.valid !== "boolean" || !Array.isArray(buildValidation.errors) || !Array.isArray(buildValidation.warnings)) {
    fail(`Android/Quest build validation shape was invalid: ${JSON.stringify(buildValidation)}.`);
  }

  const buildPreview = await callJsonTool(client, "unity.build.android", {});
  if (buildPreview.dryRun !== true || typeof buildPreview.status !== "string") {
    fail(`Android build dry run was invalid: ${JSON.stringify(buildPreview)}.`);
  }

  const compilationStart = await callJsonTool(client, "unity.compilation.wait", {
    triggerRefresh: false,
    timeoutSeconds: 120,
    maxErrorCount: 100
  });
  const compilationJob = await waitForJob(client, compilationStart, 120_000);
  if (compilationJob.status !== "succeeded" || !compilationJob.verificationSignals.includes("compilation_completed")) {
    fail(`compilation wait job failed: ${JSON.stringify(compilationJob)}.`);
  }

  await assertAssetAuthoringFlow(client);
  await assertDurableCheckpointFlow(client);
  await assertPrefabManagementFlow(client);
  await assertTestRun(client, "edit", "UnityAiE2E.EditMode");
  await assertTestRun(client, "play", "UnityAiE2E.PlayMode");
  await assertPlayModeControlFlow(client);
}

async function assertAssetAuthoringFlow(client) {
  const shader = await callJsonTool(client, "unity.assets.author", {
    dryRun: false,
    confirm: true,
    kind: "shader",
    path: "Assets/UnityAiGenerated/E2EColor.shader",
    shaderSource: `Shader "UnityAI/E2EColor"
{
    Properties { _Color ("Color", Color) = (1,0,0,1) }
    SubShader
    {
        Pass { Color [_Color] }
    }
}`
  });
  assertAssetAuthored("shader", shader, "UnityEngine.Shader");

  const material = await callJsonTool(client, "unity.assets.author", {
    dryRun: false,
    confirm: true,
    kind: "material",
    path: "Assets/UnityAiGenerated/E2EMaterial.mat",
    shaderName: "Standard",
    materialProperties: [
      { name: "_Color", kind: "color", x: 0.2, y: 0.4, z: 0.8, w: 1 }
    ]
  });
  assertAssetAuthored("material", material, "UnityEngine.Material");

  const animation = await callJsonTool(client, "unity.assets.author", {
    dryRun: false,
    confirm: true,
    kind: "animation_clip",
    path: "Assets/UnityAiGenerated/E2EMove.anim",
    frameRate: 60,
    animationCurves: [
      {
        componentType: "UnityEngine.Transform",
        propertyName: "m_LocalPosition.x",
        keyframes: [
          { time: 0, value: 0 },
          { time: 1, value: 1 }
        ]
      }
    ]
  });
  assertAssetAuthored("animation", animation, "UnityEngine.AnimationClip");

  const audio = await callJsonTool(client, "unity.assets.author", {
    dryRun: false,
    confirm: true,
    kind: "audio_tone",
    path: "Assets/UnityAiGenerated/E2ETone.wav",
    audioTone: {
      frequencyHz: 440,
      durationSeconds: 0.1,
      sampleRate: 22050,
      channels: 1,
      amplitude: 0.25
    },
    audioImport: {
      compressionFormat: "pcm",
      loadType: "decompress_on_load",
      preloadAudioData: true
    }
  });
  assertAssetAuthored("audio", audio, "UnityEngine.AudioClip");
}

function assertAssetAuthored(label, result, expectedType) {
  if (result.verificationStatus !== "passed" || result.assetType !== expectedType || !result.checkpointId || !result.verificationSignals?.includes("asset_mutation_verified")) {
    fail(`${label} asset authoring failed: ${JSON.stringify(result)}.`);
  }

  if (!existsSync(join(tempProject, result.path))) {
    fail(`${label} asset was not created at ${result.path}.`);
  }
}

async function assertDurableCheckpointFlow(client) {
  const fixturePath = "Assets/UnityAiCheckpointFixture.txt";
  const created = await callJsonTool(client, "unity.checkpoints.create", {
    dryRun: false,
    confirm: true,
    label: "e2e-checkpoint",
    paths: [fixturePath]
  });
  if (created.created !== true || created.verificationStatus !== "passed" || !created.checkpointId) {
    fail(`checkpoint creation failed: ${JSON.stringify(created)}.`);
  }

  const listed = await callJsonTool(client, "unity.checkpoints.list", {});
  if (!listed.checkpoints?.some((checkpoint) => checkpoint.checkpointId === created.checkpointId)) {
    fail("created checkpoint was not listed.");
  }

  writeFileSync(join(tempProject, fixturePath), "checkpoint-after\n");
  const restored = await callJsonTool(client, "unity.checkpoints.restore", {
    dryRun: false,
    confirm: true,
    checkpointId: created.checkpointId,
    createSafetyCheckpoint: true
  });
  if (restored.restored !== true || restored.verificationStatus !== "passed" || !restored.safetyCheckpointId) {
    fail(`checkpoint restore failed: ${JSON.stringify(restored)}.`);
  }

  if (readFileSync(join(tempProject, fixturePath), "utf8") !== "checkpoint-before\n") {
    fail("checkpoint restore did not restore the original file content.");
  }

  const deleted = await callJsonTool(client, "unity.checkpoints.delete", {
    dryRun: false,
    confirm: true,
    checkpointId: created.checkpointId
  });
  if (deleted.deleted !== true || deleted.verificationStatus !== "passed") {
    fail(`checkpoint deletion failed: ${JSON.stringify(deleted)}.`);
  }
}

async function assertPrefabManagementFlow(client) {
  const variantPath = "Assets/UnityAiGenerated/UnityAiE2EVariant.prefab";
  const variant = await callJsonTool(client, "unity.prefab.manage", {
    dryRun: false,
    confirm: true,
    action: "create_variant",
    prefabPath: "Assets/UnityAiE2E.prefab",
    targetPath: variantPath
  });
  assertPrefabManaged("create variant", variant, "Variant");

  const edited = await callJsonTool(client, "unity.prefab.manage", {
    dryRun: false,
    confirm: true,
    action: "edit_asset",
    prefabPath: variantPath,
    operations: [
      { kind: "create_child", objectPath: "", name: "ManagedChild" },
      {
        kind: "set_property",
        objectPath: "",
        componentType: "UnityEngine.BoxCollider",
        propertyPath: "m_IsTrigger",
        value: { kind: "bool", boolValue: true }
      }
    ]
  });
  assertPrefabManaged("edit variant", edited, "Variant");

  const instantiated = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      {
        kind: "instantiate_prefab",
        prefabPath: variantPath,
        name: "UnityAiManagedPrefab"
      },
      {
        kind: "set_property",
        targetPath: "UnityAiManagedPrefab",
        componentType: "UnityEngine.BoxCollider",
        propertyPath: "m_IsTrigger",
        value: { kind: "bool", boolValue: false }
      }
    ]
  });
  if (instantiated.applied !== true) {
    fail(`managed prefab instance setup failed: ${JSON.stringify(instantiated)}.`);
  }

  const applied = await callJsonTool(client, "unity.prefab.manage", {
    dryRun: false,
    confirm: true,
    action: "apply_overrides",
    sceneObjectPath: "UnityAiManagedPrefab"
  });
  assertPrefabManaged("apply overrides", applied, "Variant");

  await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      {
        kind: "set_property",
        targetPath: "UnityAiManagedPrefab",
        componentType: "UnityEngine.BoxCollider",
        propertyPath: "m_IsTrigger",
        value: { kind: "bool", boolValue: true }
      }
    ]
  });
  const reverted = await callJsonTool(client, "unity.prefab.manage", {
    dryRun: false,
    confirm: true,
    action: "revert_overrides",
    sceneObjectPath: "UnityAiManagedPrefab"
  });
  assertPrefabManaged("revert overrides", reverted, "Variant");

  const cleanup = await callJsonTool(client, "unity.scene.batch", {
    dryRun: false,
    confirm: true,
    operations: [
      { kind: "delete", targetPath: "UnityAiManagedPrefab" }
    ]
  });
  if (cleanup.applied !== true) {
    fail(`managed prefab cleanup failed: ${JSON.stringify(cleanup)}.`);
  }
}

function assertPrefabManaged(label, result, expectedType) {
  if (result.applied !== true || result.verificationStatus !== "passed" || result.prefabAssetType !== expectedType || !result.checkpointId || !result.verificationSignals?.includes("prefab_mutation_verified")) {
    fail(`${label} failed: ${JSON.stringify(result)}.`);
  }
}

async function assertTestRun(client, mode, assemblyName) {
  const started = await callJsonTool(client, "unity.tests.run", {
    dryRun: false,
    confirm: true,
    mode,
    assemblyNames: [assemblyName],
    saveModifiedScenes: true
  });
  const job = await waitForJob(client, started, 180_000);
  const result = parseJobResult(job);
  if (job.status !== "succeeded" || result.passed < 1 || result.failed !== 0 || !existsSync(join(tempProject, result.resultPath))) {
    fail(`${mode} mode test job failed: ${JSON.stringify(job)}.`);
  }
}

async function assertPlayModeControlFlow(client) {
  const initial = await callJsonTool(client, "unity.playmode.status", {});
  if (initial.isPlaying !== false) {
    fail("Play Mode should be stopped before control flow.");
  }

  const entered = await waitForJob(client, await callJsonTool(client, "unity.playmode.control", {
    dryRun: false,
    confirm: true,
    action: "enter"
  }), 120_000);
  if (entered.status !== "succeeded") {
    fail(`enter Play Mode failed: ${JSON.stringify(entered)}.`);
  }

  for (const action of ["pause", "step", "resume"]) {
    const job = await waitForJob(client, await callJsonTool(client, "unity.playmode.control", {
      dryRun: false,
      confirm: true,
      action
    }), 60_000);
    if (job.status !== "succeeded") {
      fail(`${action} Play Mode failed: ${JSON.stringify(job)}.`);
    }
  }

  const exited = await waitForJob(client, await callJsonTool(client, "unity.playmode.control", {
    dryRun: false,
    confirm: true,
    action: "exit"
  }), 120_000);
  if (exited.status !== "succeeded") {
    fail(`exit Play Mode failed: ${JSON.stringify(exited)}.`);
  }

  const final = await callJsonTool(client, "unity.playmode.status", {});
  if (final.isPlaying !== false || final.state !== "stopped") {
    fail(`Play Mode did not return to stopped: ${JSON.stringify(final)}.`);
  }
}

async function waitForJob(client, started, timeoutMs) {
  if (started.accepted !== true || typeof started.jobId !== "string") {
    fail(`job was not accepted: ${JSON.stringify(started)}.`);
  }

  const deadline = Date.now() + timeoutMs;
  let latest;
  while (Date.now() < deadline) {
    await waitForBridgeHealth(bridgeUrl, 30_000);
    latest = await callJsonToolWithRetry(client, "unity.jobs.get", { jobId: started.jobId }, 30_000);
    if (["succeeded", "failed", "cancelled"].includes(latest.status)) {
      return latest;
    }

    await delay(500);
  }

  fail(`timed out waiting for job ${started.jobId}: ${JSON.stringify(latest)}.`);
}

async function callJsonToolWithRetry(client, name, args, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let latestError = "no response";
  while (Date.now() < deadline) {
    try {
      const result = await client.callTool({ name, arguments: args });
      const text = result?.content?.find((item) => item.type === "text" && item.text.length > 0)?.text;
      if (text) {
        try {
          const parsed = JSON.parse(text);
          console.log(`✓ ${name}`);
          return parsed;
        } catch {
          latestError = text;
        }
      }
    } catch (error) {
      latestError = error instanceof Error ? error.message : String(error);
    }

    await delay(500);
  }

  fail(`Tool ${name} remained unavailable: ${latestError}`);
}

function parseJobResult(job) {
  if (typeof job.resultJson !== "string" || job.resultJson.length === 0) {
    fail(`job ${job.jobId} did not contain resultJson.`);
  }

  try {
    return JSON.parse(job.resultJson);
  } catch (error) {
    fail(`job ${job.jobId} resultJson was invalid: ${error instanceof Error ? error.message : String(error)}.`);
  }
}

function assertReadyScreenshot(label, result, expectedWidth, expectedHeight) {
  if (result.available !== true || result.ready !== true || result.width !== expectedWidth || result.height !== expectedHeight) {
    fail(`${label}: screenshot was not ready with expected dimensions: ${JSON.stringify(result)}.`);
  }

  if (result.cameraPath !== "UnityAiE2EVisualCamera") {
    fail(`${label}: expected explicit visual fixture camera, got ${result.cameraPath}.`);
  }

  if (typeof result.path !== "string" || !result.path.startsWith("UnityAIArtifacts/Screenshots/") || result.path.startsWith("/")) {
    fail(`${label}: expected a project-relative screenshot artifact path, got ${result.path}.`);
  }

  if (!existsSync(join(tempProject, result.path)) || result.byteLength <= 0 || !/^[a-f0-9]{64}$/.test(result.sha256)) {
    fail(`${label}: screenshot artifact metadata or file readiness is invalid.`);
  }

  for (const signal of ["screenshot_available", "screenshot_ready"]) {
    if (!result.verificationSignals.includes(signal)) {
      fail(`${label}: expected verification signal ${signal}.`);
    }
  }

  assertNoAbsolutePathLeakInValue(label, result);
}

function assertVisualComparison(label, result, expectedRegression) {
  if (result.compared !== true || result.dimensionsMatch !== true || result.regressionDetected !== expectedRegression) {
    fail(`${label}: unexpected visual comparison result: ${JSON.stringify(result)}.`);
  }

  const expectedSignal = expectedRegression ? "visual_regression_detected" : "visual_regression_absent";
  if (!result.verificationSignals.includes("visual_diff_checked") || !result.verificationSignals.includes(expectedSignal)) {
    fail(`${label}: missing visual verification signals.`);
  }

  if (result.diffReady !== true || typeof result.diffPath !== "string" || !result.diffPath.startsWith("UnityAIArtifacts/Screenshots/") || !existsSync(join(tempProject, result.diffPath))) {
    fail(`${label}: expected a ready visual diff artifact.`);
  }

  if (!Array.isArray(result.regressionReasons) || (expectedRegression && result.regressionReasons.length === 0) || (!expectedRegression && result.regressionReasons.length !== 0)) {
    fail(`${label}: regression reasons do not match the decision.`);
  }

  assertNoAbsolutePathLeakInValue(label, result);
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

  const hasDeterministicErrorDiagnostic = snapshot.console.topDiagnostics.some((diagnostic) => diagnostic.category === "compiler_error" || diagnostic.severity === "error");
  if (snapshot.console.diagnosticCount === 0 || !hasDeterministicErrorDiagnostic) {
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

  const sceneUpsertResponse = await fetch(`${url}/capabilities/${encodeURIComponent("unity.scene.upsert_game_object")}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ input: { name: "Unauthorized Scene Upsert", dryRun: false, confirm: true } })
  });

  if (sceneUpsertResponse.status !== 403) {
    fail(`Expected unity.scene.upsert_game_object without token to return 403, got HTTP ${sceneUpsertResponse.status}.`);
  }

  console.log("✓ unity.scene.upsert_game_object rejects missing token");

  const sceneBatchResponse = await fetch(`${url}/capabilities/${encodeURIComponent("unity.scene.batch")}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ input: { dryRun: false, confirm: true, operations: [{ kind: "create", name: "Unauthorized Batch Object" }] } })
  });

  if (sceneBatchResponse.status !== 403) {
    fail(`Expected unity.scene.batch without token to return 403, got HTTP ${sceneBatchResponse.status}.`);
  }

  console.log("✓ unity.scene.batch rejects missing token");

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
