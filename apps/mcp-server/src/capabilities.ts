import type { CapabilityManifest } from "@unity-ai/core-protocol";

export const initialCapabilities: CapabilityManifest[] = [
  {
    name: "unity.project.inspect",
    description: "Inspect Unity project metadata, active scene, packages, and high-level settings.",
    permissions: ["read_project", "read_project_settings", "read_packages", "read_scenes"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.console.read",
    description: "Read Unity Console messages and summarize errors, warnings, and logs.",
    permissions: ["read_console"],
    effects: ["report_only"],
    verification: ["console_snapshot"]
  },
  {
    name: "unity.console.diagnose",
    description: "Classify Unity Console entries and return safe, structured diagnostic guidance.",
    permissions: ["read_console"],
    effects: ["report_only"],
    verification: ["console_diagnostics"]
  },
  {
    name: "unity.assets.list",
    description: "List Unity project assets with paths, GUIDs, and main asset types.",
    permissions: ["read_project", "read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.scenes.list",
    description: "List scenes discovered in the project and Build Settings.",
    permissions: ["read_project", "read_scenes", "read_project_settings"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.scene.inspect",
    description: "Inspect the active scene hierarchy at a high level.",
    permissions: ["read_scenes"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.prefabs.list",
    description: "List prefabs in the Unity project with paths, GUIDs, and root component summaries.",
    permissions: ["read_project", "read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.prefab.inspect",
    description: "Inspect a prefab asset hierarchy and components at a high level.",
    permissions: ["read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.asset.dependencies",
    description: "Inspect direct or recursive dependencies for a Unity asset path.",
    permissions: ["read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.scripts.list",
    description: "List C# scripts visible to Unity with paths, class names, and namespaces when available.",
    permissions: ["read_project", "read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.assemblies.list",
    description: "List Unity script assemblies and assembly definition metadata.",
    permissions: ["read_project"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.packages.list",
    description: "List registered Unity packages for the current project.",
    permissions: ["read_project", "read_packages"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.project.settings.inspect",
    description: "Inspect high-level Unity project and player settings.",
    permissions: ["read_project_settings"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.vision.capture",
    description: "Capture Scene View or Game View screenshots for visual reasoning.",
    permissions: ["capture_screenshots"],
    effects: ["report_only", "write_artifacts"],
    verification: ["screenshot_available"]
  },
  {
    name: "unity.meta_xr.validate_setup",
    description: "Validate Meta XR, OpenXR, Android, and Quest readiness settings.",
    permissions: ["read_project_settings", "read_packages", "read_scenes"],
    effects: ["report_only"],
    verification: ["xr_settings_valid"]
  },
  {
    name: "unity.editor.create_empty_game_object",
    description: "Create an empty GameObject in the active scene with dry-run and audit output.",
    permissions: ["modify_scenes"],
    effects: ["report_only", "write_audit_log", "scene_change"],
    verification: ["operation_audited", "structured_observation", "scene_mutation_verified"]
  },
  {
    name: "unity.editor.undo_last_operation",
    description: "Undo the last Unity Editor operation with confirmation, audit output, and rollback verification.",
    permissions: ["modify_scenes"],
    effects: ["report_only", "write_audit_log", "scene_change"],
    verification: ["operation_audited", "structured_observation", "rollback_verified"]
  }
];
