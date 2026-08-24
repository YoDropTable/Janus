# Janus

Janus is an MCP-first, self-hosted knowledge system for the physical things you own.

The first goal is intentionally small: an agent can create an asset, attach structured facts to it, and retrieve those facts later through MCP.

## v0.1 scope

- MCP is the primary interface.
- Assets represent vehicles, equipment, appliances, bicycles, home systems, and other physical things.
- Facts attach arbitrary typed information to assets.
- Events record historical changes and maintenance.
- SQLite is the default persistence layer.
- Docker is the default deployment path.

## First acceptance test

1. Tell an MCP client: `Add my John Deere 8Y cart. Tire pressure is 30 PSI.`
2. Start a fresh client/session.
3. Ask: `What's the tire pressure for my John Deere cart?`
4. Janus returns `30 PSI` from persistent storage.

## Planned MCP tools

Initial:

- `janus_search`
- `janus_get_asset`
- `janus_create_asset`
- `janus_update_asset`
- `janus_set_fact`
- `janus_remove_fact`
- `janus_record_event`

Later:

- attachments/documents
- reminders and maintenance schedules
- relationships between assets
- optional web UI
- optional PostgreSQL

## Architecture

```text
MCP clients
   |
   v
Janus.Server
   |
   v
Janus.Core
   |
   v
Janus.Storage -> SQLite
```

Janus should remain usable without a cloud account or external AI API key. Intelligence belongs to the connecting agent; Janus owns durable physical-world knowledge.
