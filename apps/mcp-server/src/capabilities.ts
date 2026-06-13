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
    name: "unity.project.snapshot",
    description: "Capture a compact, sanitized, read-only project context pack for planning next actions.",
    permissions: ["read_project", "read_project_settings", "read_packages", "read_scenes", "read_assets", "read_console"],
    effects: ["report_only"],
    verification: ["structured_observation", "console_snapshot", "console_diagnostics"]
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
    name: "unity.console.plan_fix",
    description: "Derive conservative, read-only fix plans from Unity Console diagnostics.",
    permissions: ["read_console", "read_project"],
    effects: ["report_only"],
    verification: ["console_diagnostics", "fix_plan_generated"]
  },
  {
    name: "unity.console.apply_fix",
    description: "Apply a confirmed, checkpointed one-line replacement to a C# script under Assets.",
    permissions: ["read_console", "read_project", "modify_assets"],
    effects: ["report_only", "write_audit_log", "write_checkpoint", "asset_change"],
    verification: ["operation_audited", "checkpoint_created", "line_replacement_verified"]
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
    name: "unity.scene.inspect_game_object",
    description: "Inspect one GameObject, its components, and bounded visible serialized properties.",
    permissions: ["read_scenes", "read_assets"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.scene.upsert_game_object",
    description: "Create or update a GameObject in the active scene from a safe, schema-bound spec.",
    permissions: ["modify_scenes"],
    effects: ["report_only", "write_audit_log", "scene_change"],
    verification: ["operation_audited", "structured_observation", "scene_mutation_verified"]
  },
  {
    name: "unity.scene.batch",
    description: "Apply an atomic, undo-backed batch of scene hierarchy and serialized component operations.",
    permissions: ["read_assets", "modify_scenes"],
    effects: ["report_only", "write_audit_log", "scene_change"],
    verification: ["operation_audited", "structured_observation", "scene_mutation_verified", "batch_applied", "component_state_verified"]
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
    name: "unity.packages.change",
    description: "Add or remove registry Unity packages as a persistent checkpointed job.",
    permissions: ["read_packages", "modify_project_settings"],
    effects: ["write_checkpoint", "package_change"],
    verification: ["checkpoint_created", "packages_resolved"]
  },
  {
    name: "unity.project.settings.inspect",
    description: "Inspect high-level Unity project and player settings.",
    permissions: ["read_project_settings"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.project.settings.update",
    description: "Update selected Project, Player, Android, and Build Settings with a durable checkpoint.",
    permissions: ["read_project_settings", "modify_project_settings"],
    effects: ["write_checkpoint", "project_setting_change"],
    verification: ["checkpoint_created", "project_settings_verified"]
  },
  {
    name: "unity.jobs.get",
    description: "Read one persistent Unity operation job and its result.",
    permissions: ["read_project"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.jobs.list",
    description: "List persistent Unity operation jobs.",
    permissions: ["read_project"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.jobs.cancel",
    description: "Request cancellation of a queued or running Unity operation job.",
    permissions: ["execute_editor_operation"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.tests.run",
    description: "Run Unity Edit Mode or Play Mode tests and persist XML results.",
    permissions: ["run_tests", "write_artifacts"],
    effects: ["test_execution", "write_artifacts"],
    verification: ["tests_passed", "test_results_available"]
  },
  {
    name: "unity.playmode.status",
    description: "Read the current Unity Play Mode and pause state.",
    permissions: ["read_project"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.playmode.control",
    description: "Enter, exit, pause, resume, or step Play Mode through a persistent job.",
    permissions: ["execute_editor_operation"],
    effects: ["playmode_change"],
    verification: ["playmode_state_verified"]
  },
  {
    name: "unity.compilation.status",
    description: "Read Unity compilation/import state and current console counts.",
    permissions: ["read_project", "read_console"],
    effects: ["report_only"],
    verification: ["console_snapshot"]
  },
  {
    name: "unity.compilation.wait",
    description: "Wait for Unity compilation/import to settle and verify console errors.",
    permissions: ["read_project", "read_console", "execute_editor_operation"],
    effects: ["report_only"],
    verification: ["compilation_completed", "console_snapshot", "console_clean"]
  },
  {
    name: "unity.build.validate_android_quest",
    description: "Validate Android/Quest modules, scenes, Player Settings, architectures, and XR packages.",
    permissions: ["read_project_settings", "read_packages", "read_scenes"],
    effects: ["report_only"],
    verification: ["build_validated"]
  },
  {
    name: "unity.build.android",
    description: "Generate a validated Android APK or AAB for Quest as a persistent job.",
    permissions: ["run_build", "write_artifacts"],
    effects: ["build_execution", "write_artifacts"],
    verification: ["build_validated", "build_succeeded"]
  },
  {
    name: "unity.assets.author",
    description: "Create or edit shaders, materials, animation clips, WAV audio, and audio import settings.",
    permissions: ["read_assets", "modify_assets", "write_artifacts"],
    effects: ["write_checkpoint", "asset_change"],
    verification: ["checkpoint_created", "asset_mutation_verified"]
  },
  {
    name: "unity.prefab.manage",
    description: "Save, edit, variant, apply, and revert prefabs with durable checkpoints.",
    permissions: ["read_assets", "read_scenes", "modify_assets", "modify_scenes", "write_artifacts"],
    effects: ["write_checkpoint", "asset_change", "scene_change"],
    verification: ["checkpoint_created", "prefab_mutation_verified"]
  },
  {
    name: "unity.checkpoints.create",
    description: "Create a durable, hashed checkpoint of selected project paths.",
    permissions: ["read_project", "write_artifacts"],
    effects: ["write_artifacts", "checkpoint_change"],
    verification: ["checkpoint_created", "checkpoint_verified"]
  },
  {
    name: "unity.checkpoints.list",
    description: "List durable Unity project checkpoints.",
    permissions: ["read_artifacts"],
    effects: ["report_only"],
    verification: ["structured_observation"]
  },
  {
    name: "unity.checkpoints.restore",
    description: "Restore and hash-verify a durable project checkpoint.",
    permissions: ["modify_assets", "modify_project_settings", "write_artifacts"],
    effects: ["checkpoint_change", "asset_change", "project_setting_change"],
    verification: ["checkpoint_restored", "checkpoint_verified"]
  },
  {
    name: "unity.checkpoints.delete",
    description: "Delete a durable project checkpoint after confirmation.",
    permissions: ["write_artifacts"],
    effects: ["checkpoint_change"],
    verification: ["checkpoint_deleted"]
  },
  {
    name: "unity.vision.capture",
    description: "Synchronously capture and verify a ready Scene View or Game View screenshot artifact.",
    permissions: ["capture_screenshots", "write_artifacts"],
    effects: ["report_only", "write_artifacts"],
    verification: ["screenshot_available", "screenshot_ready"]
  },
  {
    name: "unity.vision.compare",
    description: "Compare before and after screenshots, emit a visual diff, and detect threshold-based regressions.",
    permissions: ["read_artifacts", "write_artifacts"],
    effects: ["report_only", "write_artifacts"],
    verification: ["visual_diff_checked", "visual_regression_detected", "visual_regression_absent"]
  },
  {
    name: "unity.meta_xr.validate_setup",
    description: "Validate Meta XR, OpenXR, Android, and Quest readiness settings.",
    permissions: ["read_project_settings", "read_packages", "read_scenes"],
    effects: ["report_only"],
    verification: ["xr_settings_valid"]
  },
  {
    name: "unity.meta_xr.configure",
    description: "Install and configure Unity OpenXR and Meta OpenXR for Quest, then validate the result.",
    permissions: ["read_project_settings", "read_packages", "modify_project_settings"],
    effects: ["write_checkpoint", "package_change", "project_setting_change"],
    verification: ["checkpoint_created", "xr_settings_valid", "meta_xr_configured"]
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
