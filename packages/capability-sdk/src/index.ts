import type {
  ActionPlan,
  AuditEvent,
  CapabilityManifest,
  CapabilityName,
  Observation,
  ToolRequest,
  ToolResult,
  VerificationReport
} from "@unity-ai/core-protocol";

export interface CapabilityContext {
  readonly projectPath: string;
  observe<TData>(observation: Observation<TData>): void;
  audit(event: AuditEvent): void;
  requestConfirmation(plan: ActionPlan): Promise<boolean>;
}

export interface Capability<TInput = unknown, TOutput = unknown> {
  readonly manifest: CapabilityManifest;
  plan?(request: ToolRequest<TInput>, context: CapabilityContext): Promise<ActionPlan> | ActionPlan;
  execute(request: ToolRequest<TInput>, context: CapabilityContext): Promise<ToolResult<TOutput>> | ToolResult<TOutput>;
  verify?(request: ToolRequest<TInput>, result: ToolResult<TOutput>, context: CapabilityContext): Promise<VerificationReport> | VerificationReport;
}

export function defineCapability<TInput, TOutput>(capability: Capability<TInput, TOutput>): Capability<TInput, TOutput> {
  return capability;
}

export class CapabilityRegistry {
  private readonly capabilities = new Map<CapabilityName, Capability>();

  register(capability: Capability): void {
    if (this.capabilities.has(capability.manifest.name)) {
      throw new Error(`Capability already registered: ${capability.manifest.name}`);
    }

    this.capabilities.set(capability.manifest.name, capability);
  }

  get(name: CapabilityName): Capability | undefined {
    return this.capabilities.get(name);
  }

  list(): CapabilityManifest[] {
    return [...this.capabilities.values()].map((capability) => capability.manifest);
  }
}
