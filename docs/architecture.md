# Architecture

Unity AI Control Plane is built around one product loop: **observe → act → verify**.

## System map

```text
AI Host / Agent Runner
        ↓ MCP tools
MCP Server
        ↓ local transport
Unity Editor Plugin
        ↓ Unity APIs
Unity Project + Meta XR SDK
```

## Responsibilities

| Layer | Responsibility |
|-------|----------------|
| Agents | Plan work, use tools, explain results, and avoid direct project mutation. |
| MCP server | Expose capabilities, enforce permissions, route requests, and record audit events. |
| Unity plugin | Observe and mutate Unity through approved Editor APIs. |
| Capabilities | Define stable, extensible units of behavior. |

## Design principle

The core should be small. New integrations should be added as capabilities or adapters instead of hard-coding every Unity package into the MCP server.
