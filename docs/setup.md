# Automatic Setup

Use `scripts/setup-user.mjs` to connect a local Unity project and opencode to this checkout of Unity AI Control Plane.

## Quick Path

1. Install dependencies if needed:

```bash
npm ci
```

2. Preview the setup:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode
```

3. Apply the setup and build the MCP server:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode --build --write
```

4. Restart opencode so it reloads the global config.

5. In Unity, open `Tools -> Unity AI -> Control Plane`, start the local bridge, and use the token printed by the setup script.

## What Changes

| Target | Change |
|--------|--------|
| Unity project | Adds or updates `Packages/manifest.json` with `dependencies["com.unity-ai.control-plane"] = "file:<absolute-package-path>"`. |
| opencode | Adds or updates `mcp.unity-ai` in `~/.config/opencode/opencode.json` with a local MCP command and `environment` variables. |
| Backups | Writes `.bak-YYYYMMDDHHmmss` next to each modified file before changing it. |

The script preserves unrelated JSON fields and validates JSON after writing.

## Options

| Option | Purpose |
|--------|---------|
| `--unity-project <path>` | Install or update the Unity package dependency in a Unity project manifest. |
| `--opencode` | Configure the global opencode MCP server entry. |
| `--write` | Apply changes. Without it, the script only prints the planned changes. |
| `--yes` | Alias for `--write`. |
| `--build` | Run `npm run build` before configuring opencode. If `node_modules` is missing, run `npm ci` first. |
| `--bridge-url <url>` | Set `UNITY_AI_BRIDGE_URL`. Defaults to `http://127.0.0.1:39071`. |
| `--bridge-token <token>` | Set `UNITY_AI_BRIDGE_TOKEN`. If omitted when applying with `--write --opencode`, the script generates and prints a local token. |

## Safety Notes

- The script is dry-run by default.
- It never writes inside `.pi/`, `artifacts/`, or `.unity-ai`.
- It uses absolute paths for opencode so the MCP server works regardless of opencode's current working directory.
- It does not print a user-supplied bridge token; it only prints a masked version.
- opencode config changes require an opencode restart.
