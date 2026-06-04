export type CapabilityName = `unity.${string}` | `meta_xr.${string}` | `${string}.${string}`;

export type CapabilityPermission =
  | "read_project"
  | "read_project_settings"
  | "read_packages"
  | "read_scenes"
  | "read_assets"
  | "read_console"
  | "capture_screenshots"
  | "write_artifacts"
  | "modify_scenes"
  | "modify_assets"
  | "modify_project_settings"
  | "execute_editor_operation"
  | "execute_editor_script"
  | "run_tests"
  | "run_build";

export type CapabilityEffect =
  | "report_only"
  | "write_artifacts"
  | "write_audit_log"
  | "scene_change"
  | "asset_change"
  | "code_change"
  | "project_setting_change"
  | "package_change"
  | "test_execution"
  | "build_execution";

export type VerificationSignal =
  | "structured_observation"
  | "console_snapshot"
  | "console_clean"
  | "screenshot_available"
  | "visual_diff_checked"
  | "xr_settings_valid"
  | "tests_passed"
  | "build_validated"
  | "operation_audited"
  | "scene_mutation_verified"
  | "rollback_verified";

export interface CapabilityManifest {
  readonly name: CapabilityName;
  readonly description: string;
  readonly permissions: readonly CapabilityPermission[];
  readonly effects: readonly CapabilityEffect[];
  readonly verification: readonly VerificationSignal[];
}

export interface ToolRequest<TInput = unknown> {
  readonly requestId: string;
  readonly projectPath: string;
  readonly dryRun: boolean;
  readonly input: TInput;
}

export type OperationStatus = "ok" | "needs_confirmation" | "failed";

export interface ToolResult<TOutput = unknown> {
  readonly requestId: string;
  readonly status: OperationStatus;
  readonly output?: TOutput;
  readonly error?: string;
  readonly auditEvents: readonly AuditEvent[];
  readonly verification: readonly VerificationSignal[];
}

export interface AuditEvent {
  readonly timestamp: string;
  readonly capability: CapabilityName;
  readonly requestId: string;
  readonly correlationId: string;
  readonly message: string;
  readonly effects: readonly CapabilityEffect[];
}

export type ObservationSource =
  | "project"
  | "scene"
  | "console"
  | "screenshot"
  | "meta_xr"
  | "tests"
  | "build";

export interface Observation<TData = unknown> {
  readonly source: ObservationSource;
  readonly capturedAt: string;
  readonly data: TData;
}

export interface ActionPlan {
  readonly summary: string;
  readonly requiredPermissions: readonly CapabilityPermission[];
  readonly expectedEffects: readonly CapabilityEffect[];
  readonly requiresConfirmation: boolean;
  readonly rollbackStrategy?: string;
}

export interface VerificationReport {
  readonly status: "passed" | "failed" | "inconclusive";
  readonly signals: readonly VerificationSignal[];
  readonly summary: string;
  readonly evidence: readonly Observation[];
}
