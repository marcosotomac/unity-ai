# Automatic Setup

Use `scripts/setup-user.mjs` to connect a local Unity project and MCP-capable hosts to this checkout of Unity AI Control Plane.

## Quick Path

1. Install dependencies if needed:

```bash
npm ci
```

2. Preview the setup:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode --claude-code --codex
```

3. Apply the setup and build the MCP server:

```bash
npm run setup:user -- --unity-project /path/to/UnityProject --opencode --claude-code --codex --build --write
```

4. Restart opencode or Codex so it reloads config. Claude Code is configured through its CLI.

5. In Unity, open `Tools -> Unity AI -> Control Plane`, start the local bridge, and use the token printed by the setup script.

## What Changes

| Target | Change |
|--------|--------|
| Unity project | Adds or updates `Packages/manifest.json` with `dependencies["com.unity-ai.control-plane"] = "file:<absolute-package-path>"`. |
| opencode | Adds or updates `mcp.unity-ai` in `~/.config/opencode/opencode.json` with a local MCP command and `environment` variables. |
| Claude Code | Runs `claude mcp add-json unity-ai ...` when applying with `--write`. Dry-runs print the command with supplied tokens masked or generated-token placeholders. |
| Codex | Adds or updates a generated block in `~/.codex/config.toml` with `[mcp_servers.unity-ai]`, `command`, `args`, and `[mcp_servers.unity-ai.env]`. |
| Backups | Writes `.bak-YYYYMMDDHHmmss` next to each modified file before changing it. |

The script preserves unrelated JSON fields and validates JSON after writing. For Codex TOML, it preserves existing content and only replaces the generated `unity-ai` block; if an unmarked `[mcp_servers.unity-ai]` section already exists, it stops instead of creating duplicate TOML tables.

## Options

| Option | Purpose |
|--------|---------|
| `--unity-project <path>` | Install or update the Unity package dependency in a Unity project manifest. |
| `--opencode` | Configure the global opencode MCP server entry. |
| `--claude-code` | Configure Claude Code by invoking `claude mcp add-json`. Requires `claude` on `PATH` when using `--write`. |
| `--claude-scope <local\|project\|user>` | Optional Claude Code scope. By default no scope flag is passed. |
| `--codex` | Configure Codex by writing a generated MCP block to `~/.codex/config.toml`. |
| `--write` | Apply changes. Without it, the script only prints the planned changes. |
| `--yes` | Alias for `--write`. |
| `--build` | Run `npm run build` before configuring MCP hosts. If `node_modules` is missing, run `npm ci` first. |
| `--bridge-url <url>` | Set `UNITY_AI_BRIDGE_URL`. Defaults to `http://127.0.0.1:39071`. |
| `--bridge-token <token>` | Set `UNITY_AI_BRIDGE_TOKEN`. If omitted when applying with `--write` and any MCP host, the script generates and prints a local token. |

For Codex write tests or scripted setup that must not touch the real user config, set `UNITY_AI_CODEX_CONFIG_PATH` to a temporary TOML path before running the setup script.

## Safety Notes

- The script is dry-run by default.
- It never writes inside `.pi/`, `artifacts/`, or `.unity-ai`.
- It uses absolute paths for MCP hosts so the MCP server works regardless of the host's current working directory.
- It does not print a user-supplied bridge token; it only prints a masked version.
- Generated tokens are printed only when applying with `--write`.
- Codex config is backed up before writes and only the generated block is replaced.
- Claude Code config is not edited directly; setup uses the Claude Code CLI and fails with an actionable message if `claude` is missing.
- opencode and Codex config changes require restarting the host or reloading MCP configuration.
