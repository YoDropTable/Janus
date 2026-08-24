# AGENTS.md — Janus

## Mission

Build Janus as an MCP-first, self-hosted knowledge system for physical assets and household/equipment information.

## Product principles

1. MCP is first-class. Do not make a REST API the primary product.
2. The first useful vertical slice is asset + fact persistence and search.
3. Agents should be able to use human names and aliases. Do not force callers to know database IDs for normal operations.
4. Keep deployment boring: one Docker container and SQLite by default.
5. Do not require SaaS, telemetry, SMTP, Redis, PostgreSQL, or an external LLM.
6. Preserve history when practical; avoid silently destroying useful prior facts.
7. Prefer a small flexible domain model over separate tables/classes for every asset category.
8. Public project code must contain no user- or household-specific assumptions.

## Initial domain

### Asset
A physical thing: vehicle, tractor, cart, appliance, bike, HVAC system, tool, etc.

Minimum fields:
- Id
- Name
- Type
- Manufacturer
- Model
- SerialNumber
- Description
- CreatedAt
- UpdatedAt

### Fact
A typed property attached to an asset.

Minimum fields:
- Id
- AssetId
- Key
- Value
- ValueType
- Unit
- Source
- CreatedAt
- UpdatedAt

Examples:
- tire_pressure = 30, unit psi
- tire_size = 16x6.50-8
- oil_type = 5W-30
- furnace_filter_size = 16x25x1

### Event
A dated historical record associated with an asset.

Examples:
- purchased
- oil changed
- tire replaced
- furnace serviced

## MCP design rules

- Tool descriptions must be written for model usability, not human API aesthetics.
- `janus_search` should answer common questions in one call when possible.
- Write tools should accept an asset name/alias as well as an ID.
- If an asset reference is ambiguous, return candidates rather than guessing.
- Units must remain explicit.
- Search results should return the most relevant asset facts, not only an asset ID.

## v0.1 acceptance criteria

A clean Janus instance must support this flow entirely over MCP:

1. Create asset `John Deere 8Y Cart`.
2. Set `tire_pressure = 30 psi`.
3. Restart Janus.
4. Search `John Deere cart tire pressure`.
5. Receive the persisted 30 psi fact.

## Out of scope for the first vertical slice

- Web UI
- REST CRUD surface
- Home Assistant integration
- Notifications
- Authentication beyond what is necessary to safely run locally
- Semantic/vector search
- LLM calls from Janus
