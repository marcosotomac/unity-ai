#!/usr/bin/env node
import { existsSync, mkdtempSync, rmSync, writeFileSync, mkdirSync, cpSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const repoRoot = resolve(new URL("..", import.meta.url).pathname);
const packageSource = join(repoRoot, "apps/unity-plugin/Packages/com.unity-ai.control-plane");
const defaultUnityPaths = [
  "/Applications/Unity/Hub/Editor/2022.3.*/Unity.app/Contents/MacOS/Unity",
  "/Applications/Unity/Unity.app/Contents/MacOS/Unity"
];

const unityPath = process.env.UNITY_PATH ?? process.argv[2];

if (!existsSync(packageSource)) {
  fail(`Unity package source not found: ${packageSource}`);
}

if (!unityPath) {
  fail(`Missing Unity executable path.

Set UNITY_PATH or pass it as the first argument:

  UNITY_PATH="/Applications/Unity/Hub/Editor/2022.3.x/Unity.app/Contents/MacOS/Unity" npm run verify:unity-package

Common locations:
${defaultUnityPaths.map((path) => `  - ${path}`).join("\n")}`);
}

if (!existsSync(unityPath)) {
  fail(`Unity executable does not exist: ${unityPath}`);
}

const tempProject = mkdtempSync(join(tmpdir(), "unity-ai-verify-"));
const packagesDir = join(tempProject, "Packages");
const assetsDir = join(tempProject, "Assets");
const logsDir = join(repoRoot, "artifacts/unity-verification");
const logPath = join(logsDir, "editor-compile.log");
const includeInputSystem = process.env.UNITY_AI_VERIFY_INPUT_SYSTEM === "1";

mkdirSync(packagesDir, { recursive: true });
mkdirSync(assetsDir, { recursive: true });
mkdirSync(logsDir, { recursive: true });
cpSync(packageSource, join(packagesDir, "com.unity-ai.control-plane"), { recursive: true });

if (includeInputSystem) {
  const editorDir = join(assetsDir, "Editor");
  mkdirSync(editorDir, { recursive: true });
  writeFileSync(
    join(editorDir, "UnityAiInputSystemVerification.cs"),
    `using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityAI.ControlPlane.Editor.InputSystemSupport;

public static class UnityAiInputSystemVerification
{
    public static void Run()
    {
        var queueError = InputSystemSimulationBackend.Queue("keyboard", "space", "button", 1f, 0f, 0f);
        if (!string.IsNullOrWhiteSpace(queueError))
        {
            throw new InvalidOperationException(queueError);
        }

        InputSystem.Update();
        if (Keyboard.current == null || !Keyboard.current.spaceKey.isPressed)
        {
            throw new InvalidOperationException("Virtual keyboard space press was not observed.");
        }

        var resetError = InputSystemSimulationBackend.Reset();
        if (!string.IsNullOrWhiteSpace(resetError))
        {
            throw new InvalidOperationException(resetError);
        }

        Debug.Log("UNITY_AI_INPUT_SIMULATION_VERIFIED");
        EditorApplication.Exit(0);
    }
}
`
  );
}

writeFileSync(
  join(packagesDir, "manifest.json"),
  JSON.stringify(
    {
      dependencies: {
        "com.unity-ai.control-plane": "file:com.unity-ai.control-plane",
        ...(includeInputSystem ? { "com.unity.inputsystem": process.env.UNITY_AI_INPUT_SYSTEM_VERSION ?? "1.14.0" } : {})
      }
    },
    null,
    2
  )
);

const args = [
  "-batchmode",
  "-quit",
  "-nographics",
  "-projectPath",
  tempProject,
  "-logFile",
  logPath
];
if (includeInputSystem) {
  args.push("-executeMethod", "UnityAiInputSystemVerification.Run");
}

console.log(`Verifying Unity package with: ${unityPath}`);
console.log(`Temporary project: ${tempProject}`);
console.log(`Log file: ${logPath}`);
console.log(`Input System verification: ${includeInputSystem ? "enabled" : "disabled"}`);

const result = spawnSync(unityPath, args, { stdio: "inherit" });

try {
  rmSync(tempProject, { recursive: true, force: true });
} catch {
  // Keep going; the log is the important artifact.
}

if (result.status !== 0 && !isSuccessfulUnityCompileWithShutdownCrash(logPath)) {
  fail(`Unity package verification failed. Check log: ${logPath}`);
}

if (includeInputSystem) {
  const log = existsSync(logPath) ? readFileSync(logPath, "utf8") : "";
  if (!log.includes("UNITY_AI_INPUT_SIMULATION_VERIFIED")) {
    fail(`Input System simulation verification did not complete. Check log: ${logPath}`);
  }
}

console.log(`Unity package verification passed. Log: ${logPath}`);

function fail(message) {
  console.error(message);
  process.exit(1);
}

function isSuccessfulUnityCompileWithShutdownCrash(path) {
  if (!existsSync(path)) {
    return false;
  }

  const log = readFileSync(path, "utf8");
  const hasCompileErrors = /error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure/i.test(log);
  const hasSuccessfulExit = /Exiting batchmode successfully now!|Batchmode quit successfully invoked/i.test(log);
  const hasKnownShutdownCrash = /mutex lock failed|terminating due to uncaught exception/i.test(log);

  if (hasSuccessfulExit && hasKnownShutdownCrash && !hasCompileErrors) {
    console.warn("Unity exited with a known shutdown crash after successful compile; accepting verification.");
    return true;
  }

  return false;
}
