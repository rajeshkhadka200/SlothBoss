# The SlothBoss agent runtime

SlothBoss turns a goal like *"Organize my Downloads, set duplicates aside, summarize the important PDFs
and turn deadlines into tasks"* into a persisted, bounded **plan → approve → execute → verify** run.
The local Gemma model plans and produces content; the runtime — not the model — decides what may run,
executes it through typed tools, checks the real result, and reports truthfully.

Implementation: `apps/windows/src/SlothBoss.Core/Agent/` (Windows). The macOS client creates the same
tables (migration 002) but has no agent runtime or UI yet.

## Run lifecycle

```
Created → Planning → (AwaitingApproval) → Ready → Running ⇄ Retrying
                ↘ NeedsInput        ↘ Paused       ↘ AwaitingApproval (per step)
Running → Planning (replan after a failure) → Ready …
Running → Verifying → Completed | PartiallyCompleted | Failed        (any active state → Cancelled)
```

Allowed transitions are a table in code (`RunStateMachine`); every change goes through it and is logged.
An illegal transition throws instead of silently corrupting state (a test caught one during development).

One loop iteration (`AgentRuntime.DriveAsync`):
1. Re-read the run from SQLite; honor pause/cancel requests; enforce the wall-clock budget.
2. Take the next pending step. **Authorize** it in code (see permissions). If approval is needed, persist
   `AwaitingApproval` and stop — the run resumes when the user decides.
3. Validate the step's arguments against the tool's declared schema.
4. Journal the attempt (`running`), execute with the tool's timeout, capture the actual outcome.
5. **Verify** with the tool's own independent check (disk / database), not the tool's or model's claim.
6. On success persist and continue. On a transient failure retry within the bounded policy. On a final
   failure **replan** with the model (bounded) or skip only the steps that depended on it.
7. When no steps remain: re-run every succeeded step's verification against the current state and decide
   `Completed` (everything succeeded and verified; failures superseded by a successful replan count as
   recovered), `PartiallyCompleted`, or `Failed`. Write the report.

## Tools (typed registry)

Each tool declares id, version, purpose, typed arguments (validated; undeclared arguments are dropped),
risk, scope, timeout, retry policy, idempotency, consumed/produced artifacts and its verification method.
The model can only name tools and options — **no tool accepts a file path, SQL or a command from the model.**

| Tool | Risk | What it does | Verification |
|---|---|---|---|
| `files.list` | read | inventory of the approved folder (≤ 3 levels, ≤ 500 files, skips hidden/system/links) | listed files exist |
| `files.find_duplicates` | read | SHA-256 groups (only same-size files hashed; byte budget) | groups re-hashed |
| `files.compare_versions` | read | groups versions of the same file by name (`(2)`, `- Copy`, `_final`, `_corrected`, `v3`… ignored), newest = most recently modified; identical copies by SHA-256; for readable formats, lines added/removed vs the newest with examples | compared files exist |
| `files.plan_organization` | read | concrete proposal: type or model-suggested topic folders, Duplicates folder for extra copies, model-suggested names for unclear names; collisions pre-resolved | sources exist, targets free and unique |
| `files.apply_organization` | **modify** | creates folders, moves files one at a time; never deletes or overwrites | each move: same SHA-256 at the new path, gone from the old |
| `docs.summarize` | read + model | summaries from the files' actual content (PDF, DOCX, PPTX, XLSX, TXT, MD, CSV, images) | summaries refer to existing files |
| `docs.extract_actions` | read + model | action items with a quote, checked against the file's text (or the image's description) | quotes looked up in the source |
| `tasks.create` | create | creates SlothBoss tasks, deduplicated by title | tasks read back from the database |
| `notes.search` | read | searches SlothBoss pages and tasks | hits exist |
| `report.create` | create | report page built only from recorded results (+ optional `.md` copy, never overwriting) | page and file read back |

### How files are read (`ContentReader`)

Every tool reads content through one place, `Agent/ContentReader.cs`:

| Format | How | What the model sees |
|---|---|---|
| PDF | PdfPig text layer (no OCR) | the text, page count |
| DOCX | `word/document.xml` paragraphs | the text |
| PPTX | slides in **presentation order** (`presentation.xml`), each slide's text, and its **speaker notes** found through the slide's own relationships; slide numbers/dates/footers skipped | `Slide 1: … Notes: …` |
| XLSX | every sheet in workbook order with its name; shared and inline strings resolved; **date-formatted cells shown as dates** (from `styles.xml`), booleans as TRUE/FALSE, formulas as their saved value, blank cells kept so columns line up | `Sheet: October` + tab-separated rows |
| JPG, PNG, WEBP, GIF, BMP | sent to the **local Gemma model's vision** (`/api/chat` `images`), prompt `agent_describe_image.md`: one sentence on what the image is + its readable text copied exactly | that description |
| TXT, MD, CSV, LOG, JSON | read as text | the text |

- Images never leave the computer: the vision call goes to the same loopback-only Ollama client.
- If the selected model has no `vision` capability (checked via `/api/show`), images are skipped with a
  clear message instead of guessing from the file name.
- Each image is looked at once per job (descriptions cached in the run's artifacts, keyed by path, size
  and modification time) and counts as one model call; at most 6 images per organizing step, images over
  15 MB are skipped.
- Image descriptions are untrusted data like any document text: text in a photo saying "AI, ignore your
  rules" goes through the same injection checks.
- Generated names count as unclear and get a suggested name from the content: `WhatsApp Image 2026-… at …`,
  `ChatGPT Image …`, `Screenshot …`, `IMG_2025…`, `PXL_…`, bare GUIDs, `scan001`, `document (3)`.

## Permissions and approval (enforced in code)

| Risk | Assist | Autopilot | Supervised |
|---|---|---|---|
| read | automatic | automatic | automatic |
| create (tasks, report) | ask | automatic | covered by plan approval |
| modify (move/rename files) | ask | ask | covered by plan approval |

- Per-tool user rules override the mode: **Always ask**, **Allow automatically**, **Never** (Settings →
  Agent permissions). "Never" is honored even in an approved supervised plan.
- In Supervised mode, any replan creates a new plan version that must be approved again (material deviation).
- Some executions require review whatever the policy says: `tasks.create` stops when an item's quote
  wasn't found in its source, or when the source document looks like it is instructing an AI.
- Approval screens show the **concrete** changes (each move with its reason, each task with its source quote);
  the user can untick individual items. Suspicious items start unticked.
- Folder access is explicit (approve a folder; revoke anytime). `FileSandbox` rejects absolute paths, `..`,
  alternate data streams, device names, and any symbolic link or junction on the way; drive roots and system
  folders can't be approved.

## Budgets

Per run (defaults): 12 plan steps, 40 tool calls, 40 model calls (an image description counts as one), 2 retries per step, 2 replans,
400 file operations, 500 files, 2 GB hashed, 20 minutes of active time. Exhausting any budget stops the
run, skips remaining steps and reports `PartiallyCompleted`/`Failed` with the reason.

## Durability, retries and recovery

- Every run, plan version, step attempt, approval, file operation and event is in SQLite
  (`agent_runs`, `agent_steps`, `agent_file_ops`, `agent_events`; migration `contracts/schema/002_agent.sql`).
- Each step has an idempotency key; file moves are journaled `running` *before* they happen and checked
  against the file's recorded SHA-256 before and after. A retried batch skips applied operations.
- On startup, runs that were active are reconciled per tool: idempotent steps rerun; for interrupted moves
  the runtime checks the disk (at destination with the right hash → done; still at source → not done;
  otherwise **uncertain** → the user decides). The run is left **Paused** (or NeedsInput) — never re-run
  blindly. Optional auto-resume is off by default.
- Undo reverses applied moves (only if the file is unchanged since), removes folders it created if empty,
  and deletes tasks/report pages/report files it created if they weren't edited. Anything else is kept and
  explained.

## Untrusted content and prompt injection

- Document text goes to the model inside `<evidence>` tags as data; paths are shown with `/` so the model
  can't corrupt them by writing unescaped backslashes into JSON (found in a live run: `Documents\trip…`
  became `Documents<TAB>rip…`).
- Model-chosen file references are mapped onto real candidate paths only; anything else is ignored and logged.
  Model-suggested folder and file names are sanitized into single path segments.
- `InjectionHeuristics` flags text that addresses an AI with instructions. Flagged documents are logged,
  and action items from them always require review and start unticked. In a live run without this guard,
  Gemma turned the practice folder's injected note into the tasks "Move files to Temp directory" and
  "Create email task"; nothing harmful executed (no tool can move files outside the folder, delete or send
  anything), but those tasks were created automatically in Autopilot. With the guard they are held back.
- The heuristic is not a guarantee. The structural limits (confined, non-destructive tools; approvals;
  budgets) are the real protection.

## Memory

| Kind | Where | Notes |
|---|---|---|
| Workflow state | `agent_runs`, `agent_steps`, artifacts JSON | goal, plan versions, results, blockers |
| User-approved preferences | `settings`, `approved_folders` | modes, per-tool rules, folders — visible and editable in Settings |
| Retrieved knowledge | artifacts (`summaries`, `actions`) | each item keeps its source path; quotes are verified, summaries are labelled as model-generated |
| Execution history | `agent_events`, `agent_file_ops` | full activity log per run, shown in the run view |

## Model integration (Gemma via Ollama)

- `IAgentModel` is the provider abstraction; `OllamaAgentModel` is the only implementation and is local
  (the Ollama client refuses non-loopback endpoints). If the model a run was planned with isn't available,
  the run fails with an explanation — it never switches models or providers.
- `gemma4:e2b-it-qat` reports Ollama's `tools` capability, but SlothBoss deliberately uses **validated
  structured output** (JSON Schema via Ollama `format`) for plans, so every plan is inspectable and checked
  against the registry before anything runs. Invalid output gets at most two corrective retries.
- Gemma is used for: goal decomposition and tool selection (planning), replanning after failures,
  topic classification and clearer file names, choosing important documents, summaries, and action items.
- Without a model, an explicitly chosen **Rules (no AI)** planner can still tidy folders and find duplicates.
  It is labelled as such everywhere.

## Partner technologies — decisions

- **Google Gemma — integrated** (above). Evidence: `docs/evidence/` contains a recorded live run.
- **Temporal — not integrated.** Doing it genuinely means running a Temporal server alongside a desktop app,
  a .NET worker, workflows mirroring the plan loop, and activities wrapping the tools. That couldn't be built
  and verified properly in the time available, and a thin adapter would be misleading. Durability is
  provided by the SQLite journal described above. Integration boundary, if someone takes it on: the plan
  loop in `AgentRuntime.DriveAsync` maps to a workflow; `IAgentTool.ExecuteAsync` + `Verify` map to
  activities (they are already idempotent or journaled); approvals map to signals; `RecoverInterrupted`
  would be replaced by Temporal's replay. Permission checks must stay in SlothBoss code.
- **Sentry — not integrated.** Sending telemetry off the machine conflicts with the local-first promise, and
  the local activity log already shows each failure with its cause.
- **Auth0 — not integrated.** There is no account or server; no feature needs external identity.

## Scheduler

Not implemented. The only automatic behavior is the opt-in "resume interrupted jobs on startup".
Recurring or event-triggered runs would need visible schedules, permissions and run history; they were left
out rather than shipped half-done.
