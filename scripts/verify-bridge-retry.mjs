#!/usr/bin/env node
import assert from "node:assert/strict";
import { createServer } from "node:http";

import { UnityBridgeClient } from "../apps/mcp-server/dist/unity-bridge-client.js";

const received = [];
let postAttempts = 0;
const server = createServer(async (request, response) => {
  if (request.method === "GET" && request.url === "/health") {
    response.writeHead(200, { "content-type": "application/json" });
    response.end('{"status":"ok"}');
    return;
  }

  const body = await readBody(request);
  const envelope = JSON.parse(body);
  received.push(envelope);
  postAttempts += 1;

  if (postAttempts === 1) {
    request.socket.destroy();
    return;
  }

  response.writeHead(200, { "content-type": "application/json" });
  response.end(JSON.stringify({
    ok: true,
    capability: "unity.project.inspect",
    requestId: envelope.requestId,
    correlationId: envelope.correlationId,
    resultJson: '{"status":"recovered"}'
  }));
});

await listen(server);
const address = server.address();
assert(address && typeof address === "object");

try {
  const client = new UnityBridgeClient({
    baseUrl: `http://127.0.0.1:${address.port}`,
    timeoutMs: 1_000,
    retry: {
      maxAttempts: 4,
      maxElapsedMs: 5_000,
      baseDelayMs: 10,
      maxDelayMs: 50
    }
  });
  const result = await client.call("unity.project.inspect");

  assert.equal(result.ok, true);
  assert.equal(result.attempts, 2);
  assert.equal(result.recoveredAfterRetry, true);
  assert.equal(received.length, 2);
  assert.equal(received[0].requestId, received[1].requestId);
  assert.equal(received[0].correlationId, received[1].correlationId);
  console.log("Bridge retry verification passed: the request recovered with stable idempotency IDs.");
} finally {
  await close(server);
}

function readBody(request) {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => {
      body += chunk;
    });
    request.on("end", () => resolve(body));
    request.on("error", reject);
  });
}

function listen(httpServer) {
  return new Promise((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(0, "127.0.0.1", resolve);
  });
}

function close(httpServer) {
  return new Promise((resolve, reject) => {
    httpServer.close((error) => error ? reject(error) : resolve());
  });
}
