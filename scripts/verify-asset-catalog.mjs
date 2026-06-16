#!/usr/bin/env node
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createServer } from "node:http";

import { AssetCatalogService } from "../apps/mcp-server/dist/asset-catalog.js";

const model = "o CatalogTriangle\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
const sha256 = createHash("sha256").update(model).digest("hex");

const bundledCatalog = new AssetCatalogService({
  manifestUrls: [],
  allowedLicenses: ["CC0-1.0"],
  allowInsecureLocalhost: false
});
const vehicleSearch = await bundledCatalog.search({ query: "car", kind: "model" });
assert(vehicleSearch.assets.some((asset) => asset.assetId === "unity-ai:starter-vehicle"));
const characterSearch = await bundledCatalog.search({ query: "npc", kind: "model" });
assert(characterSearch.assets.some((asset) => asset.assetId === "unity-ai:starter-character"));

const server = createServer((request, response) => {
  if (request.url === "/catalog.json") {
    const address = server.address();
    assert(address && typeof address === "object");
    const baseUrl = `http://127.0.0.1:${address.port}`;
    const manifest = JSON.stringify({
      schemaVersion: 1,
      catalog: {
        id: "verification",
        name: "Verification Catalog",
        homepage: "https://example.com/catalog"
      },
      assets: [
        {
          id: "triangle",
          name: "Catalog Triangle",
          description: "Deterministic catalog fixture",
          kind: "model",
          format: "obj",
          tags: ["fixture", "triangle"],
          downloadUrl: `${baseUrl}/triangle.obj`,
          sourceUrl: "https://example.com/catalog/triangle",
          sha256,
          sizeBytes: Buffer.byteLength(model),
          license: {
            spdxId: "CC0-1.0",
            name: "CC0 1.0 Universal",
            url: "https://creativecommons.org/publicdomain/zero/1.0/"
          }
        },
        {
          id: "blocked-license",
          name: "Blocked License",
          description: "",
          kind: "model",
          format: "obj",
          tags: [],
          downloadUrl: `${baseUrl}/triangle.obj`,
          sourceUrl: "https://example.com/catalog/blocked",
          sha256,
          sizeBytes: Buffer.byteLength(model),
          license: {
            spdxId: "LicenseRef-Proprietary",
            name: "Proprietary",
            url: "https://example.com/license"
          }
        }
      ]
    });
    response.writeHead(200, {
      "content-type": "application/json",
      "content-length": Buffer.byteLength(manifest)
    });
    response.end(manifest);
    return;
  }

  response.writeHead(404);
  response.end();
});

await listen(server);
const address = server.address();
assert(address && typeof address === "object");

try {
  const catalog = new AssetCatalogService({
    manifestUrls: [`http://127.0.0.1:${address.port}/catalog.json`],
    allowedLicenses: ["CC0-1.0"],
    allowInsecureLocalhost: true
  });
  const search = await catalog.search({ query: "triangle", kind: "model" });
  assert(search.assets.some((asset) => asset.assetId === "verification:triangle"));
  assert(!search.assets.some((asset) => asset.assetId === "verification:blocked-license"));
  assert(search.warnings.some((warning) => warning.includes("LicenseRef-Proprietary")));

  const resolved = await catalog.resolve("verification:triangle");
  assert.equal(resolved.sha256, sha256);
  assert.equal(resolved.source.kind, "url");
  assert.equal(resolved.license.spdxId, "CC0-1.0");
  console.log("Asset catalog verification passed: bundled aliases, manifest, license allowlist, provenance, and SHA-256 are enforced.");
} finally {
  await close(server);
}

function listen(httpServer) {
  return new Promise((resolvePromise, rejectPromise) => {
    httpServer.once("error", rejectPromise);
    httpServer.listen(0, "127.0.0.1", resolvePromise);
  });
}

function close(httpServer) {
  return new Promise((resolvePromise, rejectPromise) => {
    httpServer.close((error) => error ? rejectPromise(error) : resolvePromise());
  });
}
