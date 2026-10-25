# Shared contracts

Both native clients must behave identically where data and AI are concerned. Instead of sharing code,
they share these files:

| Path | What it is | How clients use it |
|---|---|---|
| `schema/001_initial.sql` | SQLite schema, constraints, cycle-prevention and FTS triggers | executed verbatim as migration 1 |
| `schema/002_agent.sql` | agent runs, steps, file-operation journal, events, approved folders | migration 2 (used by the Windows agent; macOS creates the tables) |
| `prompts/agent_*.md` | agent planning, replanning, organization, selection, summary and action prompts | embedded verbatim (Windows) |
| `schemas/content.schema.json` | page document format (`pages.content_json`) | parsers/serializers; canonical form in docs/DATA_MODEL.md |
| `schemas/action.schema.json` | AI action payloads | strict payload parsing |
| `schemas/backup.schema.json` | backup file format | export/validate/restore |
| `schemas/model-output.schema.json` | structured outputs requested from the model | each `$defs` entry is sent as Ollama `format` |
| `prompts/*.md` | system and task prompts (`{{placeholders}}` filled in a single pass) | embedded verbatim |
| `fixtures/` | conformance cases | run by both test suites |

## Changing a contract
1. Edit the file here, never a copy.
2. Schema changes need a **new** migration file (`002_….sql`); never edit an applied migration.
   Bump `schemaVersion` handling in both clients.
3. Update fixtures, run `scripts/test-windows.ps1`, and run `apps/macos/scripts/sync-contracts.sh`
   (the macOS package bundles a byte-identical copy and a test checks it).
