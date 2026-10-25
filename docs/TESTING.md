# Testing

This page records how SlothBoss is tested and **what was actually run**, as of 2026-10-24.

## How to run

```powershell
# Windows: core tests + live-editor self-test (+ live model tests when a model name is given)
powershell -File scripts/test-windows.ps1 -LiveModel gemma4:e2b-it-qat
```
```sh
# macOS (on a Mac or the macos.yml CI runner)
cd apps/macos && ./scripts/sync-contracts.sh && swift test
```

## Automated tests — Windows core (`apps/windows/tests/SlothBoss.Core.Tests`, xUnit)

| Area | What is covered |
|---|---|
| Contract fixtures | canonical JSON round-trip, Markdown export, plain-text projection, Markdown parse of export reproduces the document; model-output parse cases; citation-validation cases; valid backup; 11 invalid backups |
| Documents | strict parsing (unknown versions/types/keys rejected), unsafe links stripped (`javascript:`, `file:`, malformed page links), span normalization |
| Schema | migration sets version, reopen is idempotent, newer schema refused **without modifying the file**, foreign keys and `CHECK`s enforced in SQL |
| Pages | nesting, breadcrumbs, cycle prevention, create under missing parent, subtree delete with task unlinking, archive/restore (incl. restore-to-top-level), favorites, rename, link sync and backlinks |
| Tasks | validation, completion lifecycle, every filter, page links |
| Search | titles, bodies, tasks; updates after edit; deleted/archived items disappear; hostile and empty queries; snippet markers; title ranking |
| Actions | proposals and rejections don't mutate; approve creates linked records; approval fails cleanly when the target vanished; 11 malformed/unsafe payloads rejected; undo for every action type; undo refused after user edits; reverse-order undo chains; history |
| Backup | export → restore preserves pages, hierarchy, content, favorites, archive state, tasks, links, completion times; snapshot refuses to overwrite; export file-name sanitization |
| Ollama client | loopback endpoints accepted; LAN, cloud, look-alike hosts and non-http refused; model listing and Gemma detection; NDJSON streaming reassembly; `think:false` only when supported; missing model → `ModelNotFound`; unreachable → `OllamaUnavailable`; cancel ≠ timeout |
| Assistant (scripted HTTP stub) | no model call when nothing is retrieved; multi-page evidence; invented citations removed; uncited answers flagged; invalid output retried then failed with no side effects; recovery on retry; extraction creates proposals only, verifies quotes, skips duplicates, drops invalid dates; plan links tasks only to cited sources; draft links sources; evidence can't close its tag; retrieval budget |
| Live model (opt-in) | grounded multi-page answer cites only retrieved sources; task extraction yields valid proposals; instructions planted in a note are not followed |
| **Agent: state machine** | illegal transitions rejected (incl. by the store); legal ones allowed |
| **Agent: sandbox** | `..`, absolute, UNC, rooted, ADS, device names, trailing dots, empty paths rejected; links not followed; drive roots and system folders can't be approved; moves never overwrite; model-suggested names sanitized |
| **Agent: documents** | text extraction from the generated PDF, DOCX and TXT; unsupported formats refused |
| **Agent: slides, spreadsheets, images** | PPTX: presentation order from `presentation.xml` (not file names), notes found through each slide's relationships, slide-number/date placeholders skipped; XLSX: sheet names via workbook relationships, shared/inline strings, date styles → ISO dates, booleans, formula values, blank cells keep columns aligned; generated names (`WhatsApp Image …`, `ChatGPT Image …`, `Screenshot …`, `IMG_…`, GUIDs) count as unclear; an image is sent to the model as base64 and its description (not the bytes) is what gets summarized; without a vision model images are skipped with a clear error and no image request is made; organizing by topic uses what the image shows and looks at it once |
| **Agent: live vision + Office (opt-in)** | real Gemma reads the text in a photo of a notice; one run over a real PowerPoint-made deck, Excel-made workbook and the photo finds all three deadlines with verified quotes (evidence in `docs/evidence/live-office-image-run-*.md`) |
| **Agent: plan validation** | unknown tools and invalid arguments rejected; undeclared arguments dropped; missing prerequisites inserted / producers moved earlier; duplicates-before-organize; step cap; folder-less goals → NeedsInput |
| **Agent: file matching / injection heuristics** | model file references map only onto real candidates (slashes, quotes, bare names); practice injection flagged, ordinary documents and sentences not flagged |
| **Agent: runtime (scripted model)** | full multi-step run with approvals, exclusions and real file changes verified on disk (renames, duplicates set aside, excluded file untouched, nothing deleted, tasks and report in DB, 8 tool calls, 9 model calls); undo restores the folder and refuses to clobber an edited file; rejection changes nothing → PartiallyCompleted; collision at execution never overwrites; locked file → bounded retries (3 attempts) → PartiallyCompleted → "retry failed" completes without repeating moves and refreshes the report in place; file changed after the proposal is left alone; cancellation between operations (deterministic hook) stops after exactly 3 operations and undo restores everything; restart mid-batch reconciles with disk and resumes without duplicates; uncertain outcome → NeedsInput → user decision; budget exhaustion; malformed plans retried then fail with no side effects; model unavailable → clear failure, no silent switch, rules planner still works; failed step → replan (repeat of the failed step dropped) → Completed with the recovery noted; Supervised approves once; "Never" blocks a tool even after plan approval; malicious model output can't escape the folder; tasks from an injected document always need review and start unticked; task creation idempotent across runs; missing folder → NeedsInput → provide folder → completes |
| **Agent: live Gemma (opt-in)** | real planning + execution on the practice folder in Supervised mode; nothing lost/changed/duplicated, apply checks pass, no "password" task |

**Update 2026-10-25 (v0.4.0):** `227 passed, 0 failed` (219 offline + 8 opt-in live tests) after adding slide, spreadsheet and image reading. With `SLOTHBOSS_OLLAMA_MODEL=gemma4:e2b-it-qat` the two new live tests passed against the real model: the photo's text was read word for word, and one run over a PowerPoint deck, an Excel workbook and the photo found all three deadlines with verified quotes (`docs/evidence/live-image-read-*.md`, `docs/evidence/live-office-image-run-*.md`). The extractor was also checked against files saved by real PowerPoint and Excel, which exposed three bugs the synthetic tests had missed (Excel date serials, notes attached to the wrong slide, slide-number placeholders read as notes); all fixed and now covered by tests. Ollama accepted JPG, PNG, WEBP, GIF and BMP in a direct check.

**Earlier update 2026-10-25 (v0.3.0):** `211 passed, 0 failed` after adding web research (SerpApi, Browserbase),
voice (ElevenLabs) and the chat Home. The new partner integrations are covered by tests against stubbed
responses in each service's documented format (results, errors, quota, offline, citation checks, opt-in
gating). **They have not yet been run against the live services** — no keys were available on the
development machine. `SlothBoss.exe --selftest-online report.txt` and the Settings → Test buttons perform
one real call to each service with the user's keys.

**Previous run (2026-10-24, Windows 11, .NET 10.0.402), via `scripts/test-windows.ps1 -LiveModel gemma4:e2b-it-qat`:**
core suite `185 passed, 0 failed` (in that pass the 4 live tests return early because the variable is only
set for the live step), editor self-test `17/17`, then the live step with Ollama 0.40.7 and
`gemma4:e2b-it-qat`: `4 passed, 0 failed` (grounded answer, task extraction, injection resistance, and the
full live agent run). Without `SLOTHBOSS_OLLAMA_MODEL` the live tests are reported as passed without
running; treat them as not run in that case. The deterministic agent tests use a **test double** for the model
(scripted replies streamed through the real Ollama client); file operations in them are real, in temp folders.

The live prompt-injection test is a single scenario against one small model. It passed, but it is not
evidence that injection is impossible; the structural defenses (evidence tagging, citation validation,
typed actions, mandatory approval) are what limit the impact if a model does follow injected text.

## Automated tests — Windows editor in a live control

`SlothBoss.exe --selftest-editor <fixtures> <report>` opens a real WinUI `RichEditBox`, loads the shared
fixture, reads it back and exercises the editing commands. **Last run: 17/17 checks passed** (Debug build
and the published Release build), covering: fixture round-trip (headings, bold/italic/both, inline code,
web and page links, bullets, numbering rendered as 1./2., checklists, quote, multi-line code), heading
toggle and revert, checklist conversion and toggling, bulleted/numbered/quote/code conversion, bold +
inline code, link application, the `## ` typing shortcut, Enter behavior after headings, in checklists and
on empty list items, and plain-text paste.

## Automated tests — macOS

`apps/macos/Tests/SlothBossCoreTests` mirrors the core suite and runs the same fixtures.
**It has not been run**: no Mac was available. `.github/workflows/macos.yml` builds and tests it on a
macOS runner; until that passes, treat the macOS client as unverified.

## Manual testing performed (Windows 11, x64)

Driven through Windows UI Automation (`scripts/uia.ps1`) with screenshots, against a throwaway workspace.

| Scenario | Result |
|---|---|
| Launch, Mica window, custom title bar, window placement restored | ✅ |
| Add sample pages; nested tree; breadcrumbs; backlinks / subpages sections | ✅ |
| Editing + autosave + reopen | ✅ after fixing a real bug (below) |
| Search with highlighted snippets; open result | ✅ |
| Tasks view with filters and source-page links | ✅ |
| Ask a question needing two pages → validated, clickable citations (12.9 s) | ✅ |
| Extract tasks → reject one → approve rest → tasks saved and linked; DB shows `applied, applied, rejected` | ✅ |
| Plan tasks across the workspace; tasks linked to the cited page | ✅ |
| Summarize → "Add summary to the page" → approve → editor reloads; Undo restores exactly | ✅ |
| Ollama unreachable (endpoint on an unused port): status, guidance, Send disabled | ✅ |
| Non-local endpoint (`https://api.openai.com/v1`) refused | ✅ |
| Published self-contained build starts with no .NET runtime on `PATH` | ✅ |

### Agent jobs in the real app (real Gemma, UI Automation, throwaway workspaces and practice folders)

| Scenario | Result |
|---|---|
| Capture → Autopilot → Gemma plan → approval card lists 16 concrete changes with reasons and model-suggested names | ✅ |
| Approve → moves, summaries, actions, tasks, report → **Completed**, all independent checks pass | ✅ |
| Controlled failure: file locked before approval → 3 attempts → others applied, job continues → **Partly done** → release lock → *Retry failed steps* → **Completed**, no duplicate moves | ✅ |
| Injection file in the folder: with the guard, its two derived tasks are held, unticked, with a warning; 8 genuine tasks created, 2 skipped | ✅ (before the guard, they were created automatically — see below) |
| App killed mid-step → relaunch shows "interrupted, checked and paused" → Resume → **Completed**, nothing repeated or lost | ✅ |
| Packaged 0.2.0 build: editor self-test and launch without a .NET runtime | ✅ |

**Agent bugs found by live/UI testing and fixed:** (1) Gemma copied Windows paths into JSON with unescaped
backslashes (`\t` became a tab), so its document choice never matched — paths are now shown with `/`;
(2) tasks derived from an injected document were auto-created in Autopilot — now always held for review;
(3) the report written before a retry described the pre-retry state — retry now refreshes it in place;
(4) the failure message counted only the last attempt's operations. Unit tests caught a missing
`Ready → AwaitingApproval` transition and a plan-repair bug that created duplicate steps.

**Bugs found by this testing and fixed:**
1. *Data loss*: content loaded before the `RichEditBox` template applied lost its font sizes, and the
   next autosave stored headings as paragraphs. Content now loads only after the control is loaded, and
   saving is blocked until then. Verified by editing a page and inspecting the database.
2. `dotnet publish` omitted compiled XAML (`SlothBoss.pri`), crashing the packaged app at startup —
   fixed with `EnableMsixTooling`.
3. The assistant hid its page actions because `Frame.Navigated` fires before `OnNavigatedTo`.
4. A collapsed selection kept its old insertion format after the `## ` shortcut.
5. Encoding damage from a scripted source rewrite (icons rendered as `▯▯▯`).
6. Undoing actions in reverse order was refused; added re-basing (with tests).

**Not manually tested yet:** keyboard-only navigation of every view, screen-reader narration, high
contrast, dark theme screenshots, backup/restore through the file pickers (logic is unit-tested),
multi-monitor placement, ARM64.

## Offline & privacy

| Check | Result |
|---|---|
| Endpoint guard refuses non-loopback hosts (unit tests + UI) | ✅ |
| TCP connections of the running app sampled every 2 s during a full "plan tasks" request | only `127.0.0.1:11434` |
| Code contains no HTTP calls other than `OllamaClient`; no telemetry | ✅ (by inspection) |
| **App run with the network adapter disabled** | ❌ **not performed** — disabling the development machine's network was out of scope for this session. The connection sampling above is the evidence offered instead. |

## Final verification run

```powershell
powershell -File scripts/test-windows.ps1 -LiveModel gemma4:e2b-it-qat
```
Result recorded at the end of the session: see the "Last run" line above.
