# Architecture

SlothBoss is two native desktop applications — WinUI 3 on Windows, SwiftUI on macOS — that share one
documented contract instead of one codebase. Both store the workspace in a local SQLite file and talk only
to a local Ollama server.

```
┌──────────── Windows (C# / .NET 10) ───────────┐   ┌────────────── macOS (Swift) ───────────────┐
│ SlothBoss.App      WinUI 3 UI, RichEditBox     │   │ SlothBoss       SwiftUI UI, NSTextView      │
│ SlothBoss.Core     storage · search · AI ·     │   │ SlothBossCore   storage · search · AI ·     │
│                    actions · backup            │   │                 actions · backup            │
└───────────────┬────────────────┬──────────────┘   └───────────┬───────────────┬────────────────┘
                │ executes       │ HTTP (loopback only)         │               │
                ▼                ▼                              ▼               ▼
        contracts/ (shared)   Ollama @ 127.0.0.1:11434 ── Gemma model (local)
        schema/001_initial.sql · schemas/*.json · prompts/*.md · fixtures/
```

## The decision: native layers + shared contracts (option A)

Two approaches were compared before writing code:

| | A. Each app implements its own data/AI layer against shared contracts | B. Shared local service or library |
|---|---|---|
| Native UI | Yes | Yes |
| Extra processes | None | A background service to install, start, version and supervise on two OSes |
| IPC / packaging | None | IPC protocol, two packaging stories, port/socket conflicts |
| Duplicated code | ~2–3k lines of storage/AI logic per platform | Minimal |
| Drift risk | Mitigated by shared SQL, schemas, prompts and fixtures | Low |
| Fits the deadline | Yes | Risky |

**Chosen: A.** The duplicated logic is small and mostly mechanical. The parts where the two clients must
agree exactly are not duplicated at all — they live in `contracts/` and are executed or tested by both:

- **`contracts/schema/001_initial.sql`** is run verbatim by both clients. Full-text indexing, hierarchy
  cycle prevention, referential actions and value constraints are SQL (triggers, `CHECK`, foreign keys),
  so they behave identically on both platforms without re-implementation.
- **`contracts/schemas/`** define the page-document format, backup format, AI action payloads and the
  structured outputs requested from the model.
- **`contracts/prompts/`** are the exact prompt texts. Both clients embed the same files.
- **`contracts/fixtures/`** are conformance cases (canonical JSON ↔ Markdown ↔ plain text, model-output
  parsing, citation validation, valid/invalid backups). Both test suites run them.

A workspace created on one OS can be exported as a backup and restored on the other.

## Components (Windows reference implementation)

| Area | Code | Notes |
|---|---|---|
| Storage | `apps/windows/src/SlothBoss.Core/Storage` | One SQLite connection, serialized by a lock; every write is a transaction; `PRAGMA user_version` migrations; refuses newer schemas without modifying the file |
| Documents | `Core/Documents` | `PageDocument` (blocks + spans), canonical JSON, Markdown export/parse, link allow-list |
| Search | `Storage/SearchService.cs` | FTS5 (`unicode61`, diacritics folded), bm25 with title weighted 6×, user input quoted token-by-token so it can't inject FTS syntax |
| AI client | `Core/Ai/OllamaClient.cs` | `/api/version`, `/api/tags`, `/api/show`, streaming `/api/chat`; loopback-only endpoint; timeout vs. user-cancel distinguished |
| Retrieval | `Core/Ai/Retriever.cs` | see [Retrieval](#retrieval) |
| Assistant | `Core/Ai/AssistantService.cs` | ask, summarize, extract tasks, plan tasks, draft note |
| Actions | `Core/Actions` | typed payloads, proposal → approve/reject → apply in a transaction → verified undo |
| Backup | `Storage/BackupService.cs` | versioned JSON, full validation before any write, all-or-nothing restore |
| **Agent runtime** | `Core/Agent` | state machine, typed tool registry, `FileSandbox`, document text extraction, Gemma planner + rules planner, budgets, journaled file ops, restart reconciliation, verification, undo — see [AGENT.md](AGENT.md) |
| UI | `apps/windows/src/SlothBoss.App` | WinUI 3, Mica, custom title bar, `RichEditBox` editor, assistant pane |

The macOS client mirrors these modules in `apps/macos/Sources/SlothBossCore`.

## Rich-text editing

Neither platform embeds a browser engine. Each uses its native text system and maps it to the shared
block document (`contracts/schemas/content.schema.json`):

| Block / mark | Windows `RichEditBox` (TOM) | macOS `NSTextView` |
|---|---|---|
| Heading 1/2/3 | bold at 21/16.5/13.5 pt | bold system font at matching sizes |
| Bulleted / numbered | paragraph `ListType` | `NSTextList` |
| Checklist | `☐ ` / `☑ ` paragraph prefix | same prefix |
| Quote | 18 pt left indent, shown italic | paragraph indent |
| Code block | monospace paragraph, 12 pt indent | monospace paragraph |
| Bold / italic / inline code | character format / monospace run | font traits / monospace run |
| Links, page links | TOM hyperlinks | `.link` attribute |

Paste is forced to plain text so foreign formatting can't masquerade as structure. On Windows the mapping
is verified inside a live `RichEditBox` by the `--selftest-editor` mode (see [TESTING.md](TESTING.md)).

## Retrieval

Grounded answers use full-text retrieval, not embeddings (a deliberate scope choice for the deadline):

1. Extract up to 12 keywords from the question (lower-cased, stop words removed).
2. FTS5 `OR` query over page titles/bodies and task titles/descriptions, ranked by bm25 (12 candidates).
3. Optionally put the open page first ("Use this page").
4. Split each page's text into ~700-character chunks on line boundaries; score chunks by keyword hits
   (+0.5 if the title matches); keep the best two per page.
5. Number excerpts `S1…Sn` and stop at 8 excerpts or 6,000 characters (fits an 8k-token context with
   room for the answer). The UI shows exactly what the model was shown.
6. If nothing matches, **the model is not called** and the user is told the notes don't cover it.

## AI safety

The model can only *propose*; the application decides and executes.

- **Untrusted content.** Page text is placed inside `<evidence>` tags; the system prompt says it is data,
  not instructions. Stored text containing `<evidence>` / `</evidence>` is neutralized so a page can't
  close the block early.
- **Structured output.** Each feature sends a JSON Schema as Ollama's `format`. Constrained decoding is
  treated as a convenience: every reply is parsed and validated again; invalid replies get at most two
  corrective retries, then the request fails with nothing changed.
- **Citations.** `[S#]` markers and declared citations are checked against the sources actually retrieved
  for that request. Invented ids are removed and counted, and the UI says so. An answer with no valid
  citation is downgraded to "partial" and flagged as unverified.
- **Typed actions.** Model output never becomes SQL, file paths or commands. The app builds one of five
  payload types (`create_task`, `create_page`, `draft_page_content`, `add_page_reference`,
  `update_task`), each strictly parsed (unknown fields rejected, ids must be UUIDs, `update_task` may only
  touch status/priority/due date) and stored as a *proposal*.
- **Approval and execution.** Nothing changes until the user approves. Approval re-validates the payload
  and applies it in one transaction; if anything fails, the transaction rolls back and the proposal is
  marked *failed* with the reason. Every proposal and decision is kept in `ai_actions`.
- **Undo without clobbering.** Undo checks that the affected record's `updated_at` still equals what the
  action left behind. If the user edited it since, undo is refused with an explanation. Undoing a later
  action that restores exactly the earlier state re-bases the earlier action, so chains can be undone in
  reverse order.
- **Local only.** `OllamaClient` rejects any endpoint whose host is not `localhost` or a loopback IP.
  There is no other network code, no telemetry and no fallback model.

## Storage locations

| Platform | Workspace | App config |
|---|---|---|
| Windows | `%LOCALAPPDATA%\SlothBoss\workspace.db` | `%LOCALAPPDATA%\SlothBoss\config.json`, `window.json` |
| macOS | `~/Library/Application Support/SlothBoss/workspace.db` | same folder |

Settings inside the workspace: selected model, Ollama endpoint, timeout, theme. No credentials of any kind.

## Dependencies and licenses

| Dependency | Used by | License |
|---|---|---|
| Microsoft.Data.Sqlite 10.0.13 | Windows core | MIT |
| SQLitePCLRaw 2.1.13 (bundles SQLite with FTS5) | Windows core | Apache-2.0 (SQLite itself: public domain) |
| Windows App SDK 1.8 (WinUI 3) | Windows app | MIT |
| PdfPig 0.1.16 | Windows core (PDF text extraction; PDF generation for the practice folder) | Apache-2.0 |
| xUnit 2.9, Microsoft.NET.Test.Sdk | Windows tests | Apache-2.0 / MIT |
| System SQLite3, Foundation, SwiftUI, AppKit | macOS | Apple SDK |
| Ollama (not bundled) | runtime | MIT |
| Gemma models (not bundled) | runtime | Gemma Terms of Use |

SlothBoss is MIT-licensed. It does not redistribute any model.
