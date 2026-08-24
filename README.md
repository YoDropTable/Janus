# Janus

Janus is an MCP-first, self-hosted knowledge system for the physical things you own.

Janus lets an agent define new item types at runtime, attach validated typed properties,
and retrieve that data later through MCP. No code or database migration is needed when
a household introduces a new kind of item.

## Current scope

- MCP is the primary interface.
- Assets represent vehicles, equipment, appliances, bicycles, home systems, and other physical things.
- Facts attach arbitrary typed information to assets.
- Dynamic item types define reusable, validated custom fields stored as rows.
- Custom fields support string, integer, number, boolean, date, datetime, URL, and enum values.
- Events record historical changes and maintenance.
- SQLite is the default persistence layer.
- Docker is the default deployment path.

## First acceptance test

1. Tell an MCP client: `Add my John Deere 8Y cart. Tire pressure is 30 PSI.`
2. Start a fresh client/session.
3. Ask: `What's the tire pressure for my John Deere cart?`
4. Janus returns `30 PSI` from persistent storage.

## MCP tools

Available:

- `janus_search`
- `janus_get_asset`
- `janus_create_asset`
- `janus_update_asset`
- `janus_set_fact`
- `janus_remove_fact`
- `janus_record_event`
- `janus_list_item_types`
- `janus_get_item_type`
- `janus_create_item_type`
- `janus_update_item_type`
- `janus_delete_item_type`
- `janus_add_field_definition`
- `janus_update_field_definition`
- `janus_remove_field_definition`

Every instance includes a built-in `basic` type. Existing v0.1 databases are
automatically mapped to it at startup without losing their original type labels,
facts, aliases, or events. Custom properties are supplied as JSON values and must
match their definitions exactly; invalid types, enum values, unknown properties,
and missing required properties return correction-friendly validation errors.

The Streamable HTTP MCP endpoint is `/mcp`. Tool writes accept an asset UUID,
name, or alias. Ambiguous references return candidates and never select one
silently. Measurements keep `value` and `unit` separate so units remain
explicit in every result.

## Run with Docker

Pull the published multi-architecture image from GitHub Container Registry:

```bash
docker pull ghcr.io/sofic-ai/janus:latest
docker run --name janus \
  --restart unless-stopped \
  -p 8080:8080 \
  -v janus-data:/data \
  ghcr.io/sofic-ai/janus:latest
```

The image supports Linux AMD64 and ARM64 hosts. Builds from `main` receive the
`latest` and `sha-<commit>` tags; Git tags beginning with `v` are published with
the same tag.

To build from source instead:

```bash
docker compose up --build
```

Janus listens on `http://localhost:8080/mcp` and stores SQLite data in the
named `janus-data` volume. Set `JANUS_PORT` to publish a different host port:

```bash
JANUS_PORT=18080 docker compose up --build
```

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
