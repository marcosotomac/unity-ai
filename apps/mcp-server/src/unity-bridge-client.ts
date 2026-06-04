import { randomUUID } from "node:crypto";

export interface UnityBridgeOptions {
  readonly baseUrl: string;
  readonly timeoutMs: number;
  readonly token?: string;
}

export interface UnityBridgeResponse {
  readonly ok: boolean;
  readonly capability: string;
  readonly requestId?: string;
  readonly correlationId?: string;
  readonly resultJson?: string;
  readonly error?: string;
}

export class UnityBridgeClient {
  private readonly baseUrl: string;

  constructor(private readonly options: UnityBridgeOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
  }

  async call(capability: string, input: unknown = {}): Promise<UnityBridgeResponse> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.options.timeoutMs);
    const requestId = randomUUID();
    const correlationId = randomUUID();

    try {
      const response = await fetch(`${this.baseUrl}/capabilities/${encodeURIComponent(capability)}`, {
        method: "POST",
        headers: this.createHeaders(),
        body: JSON.stringify({ requestId, correlationId, input }),
        signal: controller.signal
      });

      const text = await response.text();
      const parsed = parseBridgeResponse(text, capability);

      if (!response.ok) {
        return {
          ok: false,
          capability,
          requestId,
          correlationId,
          error: parsed.error ?? `Unity bridge returned HTTP ${response.status}`,
          resultJson: parsed.resultJson
        };
      }

      return parsed;
    } catch (error) {
      return {
        ok: false,
        capability,
        requestId,
        correlationId,
        error: error instanceof Error ? error.message : String(error)
      };
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
