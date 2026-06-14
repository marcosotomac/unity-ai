import { randomUUID } from "node:crypto";

export interface UnityBridgeOptions {
  readonly baseUrl: string;
  readonly timeoutMs: number;
  readonly token?: string;
  readonly retry?: Partial<UnityBridgeRetryOptions>;
}

export interface UnityBridgeRetryOptions {
  readonly maxAttempts: number;
  readonly maxElapsedMs: number;
  readonly baseDelayMs: number;
  readonly maxDelayMs: number;
}

export interface UnityBridgeResponse {
  readonly ok: boolean;
  readonly capability: string;
  readonly requestId?: string;
  readonly correlationId?: string;
  readonly resultJson?: string;
  readonly error?: string;
  readonly attempts?: number;
  readonly recoveredAfterRetry?: boolean;
}

interface BridgeAttemptResult {
  readonly response: UnityBridgeResponse;
  readonly retryable: boolean;
  readonly retryAfterMs?: number;
}

const defaultRetryOptions: UnityBridgeRetryOptions = {
  maxAttempts: 12,
  maxElapsedMs: 90_000,
  baseDelayMs: 150,
  maxDelayMs: 3_000
};

export class UnityBridgeClient {
  private readonly baseUrl: string;
  private readonly retry: UnityBridgeRetryOptions;
  private reconnecting?: Promise<boolean>;

  constructor(private readonly options: UnityBridgeOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.retry = {
      maxAttempts: positiveInteger(options.retry?.maxAttempts, defaultRetryOptions.maxAttempts),
      maxElapsedMs: positiveInteger(options.retry?.maxElapsedMs, defaultRetryOptions.maxElapsedMs),
      baseDelayMs: positiveInteger(options.retry?.baseDelayMs, defaultRetryOptions.baseDelayMs),
      maxDelayMs: positiveInteger(options.retry?.maxDelayMs, defaultRetryOptions.maxDelayMs)
    };
  }

  async call(capability: string, input: unknown = {}): Promise<UnityBridgeResponse> {
    const requestId = randomUUID();
    const correlationId = randomUUID();
    const requestBody = JSON.stringify({ requestId, correlationId, input });
    const startedAt = Date.now();
    const deadline = startedAt + this.retry.maxElapsedMs;
    let lastResponse: UnityBridgeResponse | undefined;
    let attemptsMade = 0;

    for (let attempt = 1; attempt <= this.retry.maxAttempts && Date.now() <= deadline; attempt += 1) {
      attemptsMade = attempt;
      if (this.reconnecting && !(await this.reconnecting)) {
        break;
      }

      const result = await this.attemptCall(capability, requestId, correlationId, requestBody);
      lastResponse = result.response;

      if (!result.retryable) {
        return {
          ...result.response,
          requestId: result.response.requestId ?? requestId,
          correlationId: result.response.correlationId ?? correlationId,
          attempts: attempt,
          recoveredAfterRetry: attempt > 1
        };
      }

      if (attempt >= this.retry.maxAttempts || Date.now() >= deadline) {
        break;
      }

      const available = await this.waitForReconnect(deadline, result.retryAfterMs);
      if (!available) {
        break;
      }
    }

    const elapsedMs = Date.now() - startedAt;
    return {
      ok: false,
      capability,
      requestId,
      correlationId,
      attempts: Math.max(1, attemptsMade),
      recoveredAfterRetry: false,
      error: `Unity bridge remained unavailable after ${Math.max(1, attemptsMade)} attempt(s) over ${elapsedMs}ms. ${lastResponse?.error ?? "No bridge response was received."}`
    };
  }

  private async attemptCall(
    capability: string,
    requestId: string,
    correlationId: string,
    requestBody: string
  ): Promise<BridgeAttemptResult> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.options.timeoutMs);

    try {
      const response = await fetch(`${this.baseUrl}/capabilities/${encodeURIComponent(capability)}`, {
        method: "POST",
        headers: this.createHeaders(),
        body: requestBody,
        signal: controller.signal
      });
      const text = await response.text();
      const parsed = parseBridgeResponse(text, capability);
      const bridgeResponse = response.ok
        ? parsed
        : {
            ok: false,
            capability,
            requestId,
            correlationId,
            error: parsed.error ?? `Unity bridge returned HTTP ${response.status}`,
            resultJson: parsed.resultJson
          };

      return {
        response: bridgeResponse,
        retryable: isRetryableStatus(response.status),
        retryAfterMs: parseRetryAfter(response.headers.get("retry-after"))
      };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      return {
        response: {
          ok: false,
          capability,
          requestId,
          correlationId,
          error: message
        },
        retryable: true
      };
    } finally {
      clearTimeout(timeout);
    }
  }

  private async waitForReconnect(deadline: number, minimumDelayMs = 0): Promise<boolean> {
    if (this.reconnecting) {
      return this.reconnecting;
    }

    const reconnecting = this.probeUntilAvailable(deadline, minimumDelayMs);
    this.reconnecting = reconnecting;
    try {
      return await reconnecting;
    } finally {
      if (this.reconnecting === reconnecting) {
        this.reconnecting = undefined;
      }
    }
  }

  private async probeUntilAvailable(deadline: number, minimumDelayMs: number): Promise<boolean> {
    let delayMs = Math.max(this.retry.baseDelayMs, minimumDelayMs);

    while (Date.now() < deadline) {
      await delay(Math.min(delayMs, Math.max(0, deadline - Date.now())));
      if (Date.now() >= deadline) {
        return false;
      }

      if (await this.isHealthy()) {
        return true;
      }

      delayMs = Math.min(this.retry.maxDelayMs, Math.ceil(delayMs * 1.7));
    }

    return false;
  }

  private async isHealthy(): Promise<boolean> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), Math.min(this.options.timeoutMs, 2_000));

    try {
      const response = await fetch(`${this.baseUrl}/health`, {
        method: "GET",
        headers: this.createHeaders(),
        signal: controller.signal
      });
      return response.ok;
    } catch {
      return false;
    } finally {
      clearTimeout(timeout);
    }
  }

  private createHeaders(): HeadersInit {
    const headers: Record<string, string> = { "content-type": "application/json" };

    if (this.options.token && isLocalBridgeUrl(this.baseUrl)) {
      headers["x-unity-ai-bridge-token"] = this.options.token;
    }

    return headers;
  }
}

function isRetryableStatus(status: number): boolean {
  return status === 408 || status === 425 || status === 429 || status === 502 || status === 503 || status === 504;
}

function parseRetryAfter(value: string | null): number | undefined {
  if (!value) {
    return undefined;
  }

  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) {
    return Math.min(30_000, Math.ceil(seconds * 1_000));
  }

  const date = Date.parse(value);
  if (Number.isNaN(date)) {
    return undefined;
  }

  return Math.min(30_000, Math.max(0, date - Date.now()));
}

function positiveInteger(value: number | undefined, fallback: number): number {
  return Number.isFinite(value) && Number(value) > 0 ? Math.floor(Number(value)) : fallback;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function isLocalBridgeUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === "http:" && (url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]");
  } catch {
    return false;
  }
}

function parseBridgeResponse(text: string, capability: string): UnityBridgeResponse {
  if (text.trim().length === 0) {
    return { ok: false, capability, error: "Unity bridge returned an empty response." };
  }

  try {
    return JSON.parse(text) as UnityBridgeResponse;
  } catch {
    return {
      ok: false,
      capability,
      error: "Unity bridge returned non-JSON content.",
      resultJson: text
    };
  }
}
