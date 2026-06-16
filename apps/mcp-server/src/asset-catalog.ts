import { createHash } from "node:crypto";
import { lookup } from "node:dns/promises";
import { existsSync, readFileSync, statSync } from "node:fs";
import { isIP } from "node:net";
import { dirname, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { z } from "zod";

const supportedFormats = [
  "fbx", "obj", "dae", "3ds", "dxf", "glb", "gltf",
  "png", "jpg", "jpeg", "tga", "tif", "tiff", "psd", "exr", "hdr",
  "wav", "mp3", "ogg", "aif", "aiff"
] as const;
const supportedKinds = ["model", "texture", "audio"] as const;
const maximumManifestBytes = 2 * 1024 * 1024;
const maximumAssetBytes = 2 * 1024 * 1024 * 1024;
const cacheDurationMs = 5 * 60 * 1_000;
const queryAliasGroups = [
  ["vehicle", "car", "auto", "automobile", "truck", "van", "racing", "transport"],
  ["character", "humanoid", "avatar", "player", "npc", "enemy", "person"],
  ["environment", "level", "platform", "floor", "ground", "stage", "terrain"],
  ["foliage", "tree", "plant", "vegetation", "nature"],
  ["prop", "crate", "box", "pickup", "collectible", "item"],
  ["audio", "sound", "sfx", "music"],
  ["texture", "material", "surface", "albedo"]
] as const;
const httpsUrlSchema = z.string().url().max(4096).refine(
  (value) => new URL(value).protocol === "https:",
  "Expected an HTTPS URL."
);

const licenseSchema = z.object({
  spdxId: z.string().min(1).max(64),
  name: z.string().min(1).max(160),
  url: httpsUrlSchema,
  attribution: z.string().max(1000).optional()
}).strict();

const manifestSchema = z.object({
  schemaVersion: z.literal(1),
  catalog: z.object({
    id: z.string().regex(/^[a-z0-9][a-z0-9._-]{1,63}$/),
    name: z.string().min(1).max(160),
    homepage: httpsUrlSchema
  }).strict(),
  assets: z.array(z.object({
    id: z.string().regex(/^[a-z0-9][a-z0-9._-]{1,127}$/),
    name: z.string().min(1).max(160),
    description: z.string().max(2000).default(""),
    kind: z.enum(supportedKinds),
    format: z.enum(supportedFormats),
    tags: z.array(z.string().min(1).max(64)).max(50).default([]),
    downloadUrl: z.string().url().max(4096),
    sourceUrl: httpsUrlSchema,
    sha256: z.string().regex(/^[a-fA-F0-9]{64}$/),
    sizeBytes: z.number().int().min(1).max(maximumAssetBytes),
    license: licenseSchema
  }).strict()).max(10_000)
}).strict();

export interface AssetCatalogOptions {
  readonly manifestUrls: readonly string[];
  readonly allowedLicenses: readonly string[];
  readonly allowInsecureLocalhost: boolean;
}

export interface CatalogSearchInput {
  readonly query?: string;
  readonly kind?: "all" | typeof supportedKinds[number];
  readonly tags?: readonly string[];
  readonly maxResults?: number;
  readonly refresh?: boolean;
}

export interface PublicCatalogAsset {
  readonly assetId: string;
  readonly catalogId: string;
  readonly catalogName: string;
  readonly catalogHomepage: string;
  readonly name: string;
  readonly description: string;
  readonly kind: typeof supportedKinds[number];
  readonly format: typeof supportedFormats[number];
  readonly tags: readonly string[];
  readonly sourceUrl: string;
  readonly downloadUrl?: string;
  readonly sha256: string;
  readonly sizeBytes: number;
  readonly license: {
    readonly spdxId: string;
    readonly name: string;
    readonly url: string;
    readonly attribution?: string;
  };
}

export interface ResolvedCatalogAsset extends PublicCatalogAsset {
  readonly source:
    | { readonly kind: "local"; readonly path: string }
    | { readonly kind: "url"; readonly url: string; readonly allowInsecureLocalhost: boolean };
}

interface CatalogLoadResult {
  readonly assets: ResolvedCatalogAsset[];
  readonly warnings: string[];
}

interface CatalogCacheEntry {
  readonly expiresAt: number;
  readonly assets: ResolvedCatalogAsset[];
}

export class AssetCatalogService {
  private readonly allowedLicenses: Set<string>;
  private readonly remoteCache = new Map<string, CatalogCacheEntry>();

  constructor(private readonly options: AssetCatalogOptions) {
    this.allowedLicenses = new Set(options.allowedLicenses.map((value) => value.trim()).filter(Boolean));
  }

  async search(input: CatalogSearchInput = {}) {
    const loaded = await this.loadAll(input.refresh === true);
    const queryGroups = expandQueryGroups(input.query);
    const kind = input.kind ?? "all";
    const tags = (input.tags ?? []).map(normalize).filter(Boolean);
    const maxResults = clampInteger(input.maxResults ?? 50, 1, 200);
    const matching = loaded.assets.filter((asset) => {
      if (kind !== "all" && asset.kind !== kind) {
        return false;
      }

      const searchable = normalize([
        asset.assetId,
        asset.name,
        asset.description,
        asset.catalogName,
        ...asset.tags
      ].join(" "));
      if (queryGroups.length > 0 && !queryGroups.every((group) => group.some((term) => searchable.includes(term)))) {
        return false;
      }

      const assetTags = new Set(asset.tags.map(normalize));
      return tags.every((tag) => assetTags.has(tag));
    });

    return {
      totalFound: matching.length,
      returned: Math.min(matching.length, maxResults),
      truncated: matching.length > maxResults,
      allowedLicenses: [...this.allowedLicenses].sort(),
      warnings: loaded.warnings,
      assets: matching.slice(0, maxResults).map(toPublicCatalogAsset)
    };
  }

  async resolve(assetId: string, refresh = false): Promise<ResolvedCatalogAsset> {
    const loaded = await this.loadAll(refresh);
    const asset = loaded.assets.find((candidate) => candidate.assetId === assetId);
    if (!asset) {
      const warningSuffix = loaded.warnings.length > 0 ? ` Catalog warnings: ${loaded.warnings.join(" | ")}` : "";
      throw new Error(`Catalog asset '${assetId}' was not found.${warningSuffix}`);
    }

    return asset;
  }

  private async loadAll(refresh: boolean): Promise<CatalogLoadResult> {
    const assets = loadBundledAssets();
    const warnings: string[] = [];

    for (const manifestUrl of this.options.manifestUrls) {
      try {
        assets.push(...await this.loadRemoteManifest(manifestUrl, refresh));
      } catch (error) {
        warnings.push(`${manifestUrl}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    const unique = new Map<string, ResolvedCatalogAsset>();
    for (const asset of assets) {
      if (!this.allowedLicenses.has(asset.license.spdxId)) {
        warnings.push(`${asset.assetId}: license ${asset.license.spdxId} is not in the allowlist.`);
        continue;
      }

      if (unique.has(asset.assetId)) {
        warnings.push(`${asset.assetId}: duplicate catalog asset id was ignored.`);
        continue;
      }

      unique.set(asset.assetId, asset);
    }

    return {
      assets: [...unique.values()].sort((left, right) => left.assetId.localeCompare(right.assetId)),
      warnings
    };
  }

  private async loadRemoteManifest(manifestUrl: string, refresh: boolean): Promise<ResolvedCatalogAsset[]> {
    const cached = this.remoteCache.get(manifestUrl);
    if (!refresh && cached && cached.expiresAt > Date.now()) {
      return cached.assets;
    }

    const parsed = manifestSchema.parse(await fetchJsonManifest(manifestUrl, this.options.allowInsecureLocalhost));
    const assets = parsed.assets.map((asset): ResolvedCatalogAsset => ({
      assetId: `${parsed.catalog.id}:${asset.id}`,
      catalogId: parsed.catalog.id,
      catalogName: parsed.catalog.name,
      catalogHomepage: parsed.catalog.homepage,
      name: asset.name,
      description: asset.description,
      kind: asset.kind,
      format: asset.format,
      tags: asset.tags,
      sourceUrl: asset.sourceUrl,
      downloadUrl: validateDownloadUrl(asset.downloadUrl, this.options.allowInsecureLocalhost),
      sha256: asset.sha256.toLowerCase(),
      sizeBytes: asset.sizeBytes,
      license: asset.license,
      source: {
        kind: "url",
        url: validateDownloadUrl(asset.downloadUrl, this.options.allowInsecureLocalhost),
        allowInsecureLocalhost: isLoopbackUrl(asset.downloadUrl) && this.options.allowInsecureLocalhost
      }
    }));
    this.remoteCache.set(manifestUrl, { assets, expiresAt: Date.now() + cacheDurationMs });
    return assets;
  }
}

function loadBundledAssets(): ResolvedCatalogAsset[] {
  const catalogRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../catalog-assets");
  return [
    bundledAsset(catalogRoot, {
      id: "starter-crate",
      name: "Starter Crate",
      description: "A lightweight CC0 OBJ crate for validating model import, prefab, and gameplay workflows.",
      fileName: "starter-crate.obj",
      tags: ["crate", "prop", "prototype", "environment"]
    }),
    bundledAsset(catalogRoot, {
      id: "starter-ramp",
      name: "Starter Ramp",
      description: "A lightweight CC0 OBJ ramp for level blockout and physics workflow validation.",
      fileName: "starter-ramp.obj",
      tags: ["ramp", "level", "prototype", "environment"]
    }),
    bundledAsset(catalogRoot, {
      id: "starter-vehicle",
      name: "Starter Vehicle",
      description: "A lightweight CC0 low-poly vehicle blockout with body and wheels for racing, traffic, transport, and gameplay prototype workflows.",
      fileName: "starter-vehicle.obj",
      tags: ["vehicle", "car", "truck", "racing", "transport", "prototype", "blockout"]
    }),
    bundledAsset(catalogRoot, {
      id: "starter-character",
      name: "Starter Character",
      description: "A lightweight CC0 humanoid character blockout for player, NPC, enemy, and animation placeholder workflows.",
      fileName: "starter-character.obj",
      tags: ["character", "humanoid", "avatar", "player", "npc", "enemy", "prototype", "blockout"]
    }),
    bundledAsset(catalogRoot, {
      id: "starter-tree",
      name: "Starter Tree",
      description: "A lightweight CC0 tree and foliage blockout for environment dressing and level composition.",
      fileName: "starter-tree.obj",
      tags: ["tree", "plant", "foliage", "vegetation", "nature", "environment", "prototype"]
    }),
    bundledAsset(catalogRoot, {
      id: "starter-platform",
      name: "Starter Platform",
      description: "A lightweight CC0 modular floor platform for level blockout, spawn areas, and obstacle layouts.",
      fileName: "starter-platform.obj",
      tags: ["platform", "floor", "ground", "level", "environment", "terrain", "prototype", "modular"]
    })
  ];
}

function bundledAsset(
  catalogRoot: string,
  definition: { id: string; name: string; description: string; fileName: string; tags: string[] }
): ResolvedCatalogAsset {
  const path = resolve(catalogRoot, definition.fileName);
  if (!existsSync(path)) {
    throw new Error(`Bundled catalog asset is missing: ${path}`);
  }

  const bytes = readFileSync(path);
  return {
    assetId: `unity-ai:${definition.id}`,
    catalogId: "unity-ai",
    catalogName: "Unity AI Starter Catalog",
    catalogHomepage: "https://github.com/marcosotomac/unity-ai",
    name: definition.name,
    description: definition.description,
    kind: "model",
    format: extname(definition.fileName).slice(1).toLowerCase() as "obj",
    tags: definition.tags,
    sourceUrl: `https://github.com/marcosotomac/unity-ai/tree/main/apps/mcp-server/catalog-assets/${definition.fileName}`,
    sha256: createHash("sha256").update(bytes).digest("hex"),
    sizeBytes: statSync(path).size,
    license: {
      spdxId: "CC0-1.0",
      name: "CC0 1.0 Universal",
      url: "https://creativecommons.org/publicdomain/zero/1.0/"
    },
    source: { kind: "local", path }
  };
}

function validateDownloadUrl(rawUrl: string, allowInsecureLocalhost: boolean): string {
  const url = new URL(rawUrl);
  if (url.username || url.password) {
    throw new Error("Catalog asset download URLs cannot include credentials.");
  }

  const loopback = isLoopbackUrl(url.toString());
  if (url.protocol !== "https:" && !(allowInsecureLocalhost && loopback && url.protocol === "http:")) {
    throw new Error("Catalog asset downloads require HTTPS; HTTP is only allowed for explicit localhost testing.");
  }

  return url.toString();
}

function expandQueryGroups(rawQuery: string | undefined): string[][] {
  const normalized = normalize(rawQuery);
  if (!normalized) {
    return [];
  }

  return normalized
    .split(/[^a-z0-9._-]+/u)
    .map((term) => term.trim())
    .filter(Boolean)
    .map((term) => {
      const group = queryAliasGroups.find((aliases) => (aliases as readonly string[]).includes(term));
      return [...new Set([term, ...(group ?? [])])];
    });
}

async function fetchJsonManifest(rawUrl: string, allowInsecureLocalhost: boolean): Promise<unknown> {
  let current = await validateManifestUrl(rawUrl, allowInsecureLocalhost);

  for (let redirect = 0; redirect <= 5; redirect += 1) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10_000);
    try {
      const response = await fetch(current, {
        method: "GET",
        redirect: "manual",
        headers: {
          accept: "application/json",
          "user-agent": "UnityAI-ControlPlane/0.1 asset-catalog"
        },
        signal: controller.signal
      });

      if (response.status >= 300 && response.status < 400) {
        const location = response.headers.get("location");
        if (!location || redirect === 5) {
          throw new Error("Catalog manifest exceeded the redirect limit or returned an invalid redirect.");
        }

        current = await validateManifestUrl(new URL(location, current).toString(), allowInsecureLocalhost);
        continue;
      }

      if (!response.ok) {
        throw new Error(`Catalog manifest returned HTTP ${response.status}.`);
      }

      const contentLength = Number(response.headers.get("content-length") ?? 0);
      if (contentLength > maximumManifestBytes) {
        throw new Error(`Catalog manifest exceeds ${maximumManifestBytes} bytes.`);
      }

      const bytes = new Uint8Array(await response.arrayBuffer());
      if (bytes.byteLength === 0 || bytes.byteLength > maximumManifestBytes) {
        throw new Error(`Catalog manifest size must be between 1 and ${maximumManifestBytes} bytes.`);
      }

      return JSON.parse(new TextDecoder().decode(bytes));
    } finally {
      clearTimeout(timeout);
    }
  }

  throw new Error("Catalog manifest could not be loaded.");
}

async function validateManifestUrl(rawUrl: string, allowInsecureLocalhost: boolean): Promise<string> {
  const url = new URL(rawUrl);
  if (url.username || url.password) {
    throw new Error("Catalog manifest URLs cannot include credentials.");
  }

  const loopback = isLoopbackUrl(url.toString());
  if (url.protocol !== "https:" && !(allowInsecureLocalhost && loopback && url.protocol === "http:")) {
    throw new Error("Catalog manifests require HTTPS; HTTP is only allowed for explicit localhost testing.");
  }

  if (!loopback) {
    const addresses = await lookup(url.hostname, { all: true });
    if (addresses.length === 0 || addresses.some((entry) => isPrivateAddress(entry.address))) {
      throw new Error("Catalog manifest host resolves to a private or link-local address.");
    }
  }

  return url.toString();
}

function isLoopbackUrl(rawUrl: string): boolean {
  const url = new URL(rawUrl);
  return url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]" || url.hostname === "::1";
}

function isPrivateAddress(address: string): boolean {
  if (isIP(address) === 4) {
    const parts = address.split(".").map(Number);
    return parts[0] === 10
      || parts[0] === 127
      || (parts[0] === 169 && parts[1] === 254)
      || (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31)
      || (parts[0] === 192 && parts[1] === 168);
  }

  const normalized = address.toLowerCase();
  return normalized === "::1"
    || normalized.startsWith("fc")
    || normalized.startsWith("fd")
    || normalized.startsWith("fe8")
    || normalized.startsWith("fe9")
    || normalized.startsWith("fea")
    || normalized.startsWith("feb")
    || normalized.startsWith("::ffff:127.");
}

export function toPublicCatalogAsset(asset: ResolvedCatalogAsset): PublicCatalogAsset {
  const { source: _source, ...publicAsset } = asset;
  return publicAsset;
}

function normalize(value: string | undefined): string {
  return (value ?? "").trim().toLowerCase();
}

function clampInteger(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, Math.floor(value)));
}

export function parseCatalogList(value: string | undefined): string[] {
  return (value ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

export function parseAllowedLicenses(value: string | undefined): string[] {
  const parsed = parseCatalogList(value);
  return parsed.length > 0 ? parsed : ["CC0-1.0"];
}
