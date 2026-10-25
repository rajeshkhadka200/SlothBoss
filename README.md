# SlothBoss 🦥

**Tell it what you want. It does the boring part.**

SlothBoss is a desktop assistant for the chores everyone postpones: organizing a messy Downloads folder,
reading a pile of documents and pulling out what you need to do, or researching a topic and writing a
short report with sources. You type it — or say it — and SlothBoss plans the work, does it, checks the
result, and asks you before anything important changes.

The AI is **Google's open Gemma model running on your own computer** (through Ollama). Your files never
leave your PC.

> Built for the Hacktoberfest 2026 DEV Open-Source AI Challenge, Week 3 "Slow and Steady".

## What you can ask

| You say | SlothBoss does |
|---|---|
| "Research the best open-source AI tools for a desktop assistant and recommend one." | Searches Google (SerpApi), reads the best pages (Browserbase), and Gemma writes a short report. Every claim links to a source and is labelled *confirmed in a page*, *search snippet only* or *uncertain*. Fake citations are removed. |
| "Organize my Downloads folder and set duplicates aside." | Proposes folders, clearer names for files like `scan001.pdf` or `WhatsApp Image 2026-…jpeg` (from what's inside them), and a *Duplicates* folder — shows you every change, then does it after you approve. Never deletes or overwrites. One-click undo. |
| "Summarize these files and list what I need to do." | Reads PDFs, Word documents, **PowerPoint slides (with speaker notes), Excel spreadsheets (every sheet, real dates)** and text — and **looks at photos and screenshots** with Gemma's vision to read the text in them. Summarizes everything and turns deadlines into tasks, each with the quote it came from. |
| "Which copy of my paper is the latest, and what changed?" | Finds versions of the same file (`paper.docx`, `paper (3).docx`, `Emission_factors_corrected.xlsx`…), says which is newest, which copies are identical, and what changed — with example lines. Can set older versions aside in an *Older versions* folder. |
| 🎤 Press **Speak** and say any of the above | ElevenLabs turns your voice into text. |

## Get started (Windows, ~15 minutes once)

Full step-by-step guide with troubleshooting: **[docs/INSTALL.md](docs/INSTALL.md)**.

1. **Download** `SlothBoss-…-win-x64.zip` from the [latest release](https://github.com/rajeshkhadka200/SlothBoss/releases/latest), right-click → **Extract All**, run `SlothBoss.exe`. No installer or .NET needed. (It isn't code-signed yet, so Windows may ask: *More info → Run anyway*.)
2. **Install the local AI:** get [Ollama](https://ollama.com/download), then run `ollama pull gemma4:e2b-it-qat` (≈4.3 GB, one time).
3. **Optional extras — open Settings and paste your free keys:**
   - **Web research:** a [SerpApi](https://serpapi.com) key and a [Browserbase](https://www.browserbase.com) key.
   - **Voice:** an [ElevenLabs](https://elevenlabs.io) key.

   Each has a **Test** button. Keys are encrypted on your PC (Windows DPAPI).
4. On Home, type or say what you want and press **Send**. Want to try safely first? **… → Create a practice folder** makes a messy sample folder with fictional files.

**Mac:** not available yet. A native macOS version of the notes part exists in the source but hasn't been built or tested, and has no assistant features.

## How it stays safe

- **Asks first.** Pick *Ask me first*, *Autopilot* (does reading/research/tasks alone, asks before touching files) or *Approve plan once*.
- **Can't do damage.** It only works in a folder you approve; it can read, sort, rename and create — never delete, overwrite, send email or run programs.
- **Checks its work.** Moved files are re-hashed at their new location, created tasks are read back, citations are matched to the sources actually retrieved. "Done" means checked.
- **Not fooled by files or web pages.** Text that says "AI, ignore your rules…" is treated as data; anything derived from it waits for your review.
- **Private by default.** Gemma runs locally. Web research only happens if you add keys (search queries go to SerpApi, page addresses to Browserbase). Voice audio goes to ElevenLabs only when you press Speak. No accounts, no telemetry.

## Open-source AI and partner technology

| Technology | What it does in SlothBoss | Where |
|---|---|---|
| **Gemma** (open weights, via Ollama) | Plans each job, picks tools, re-plans after failures, reads documents, **looks at images (vision)**, suggests names, summarizes, extracts to-dos, writes research reports | `SlothBoss.Core/Agent` (`ContentReader.cs`, `DocumentText.cs`) |
| **SerpApi** | Real Google search results with titles and URLs | `Agent/Web/WebClients.cs`, tool `web.search` |
| **Browserbase** (Fetch API) | Reads the full text of result pages so claims can be checked | `Agent/Web/WebClients.cs`, tool `web.read` |
| **ElevenLabs** (Speech to Text) | Voice commands | `Agent/Web/ElevenLabsClient.cs`, `App/VoiceInput.cs` |

Not used: Temporal (durability is handled by SlothBoss's own SQLite job journal), Sentry (would send telemetry), Auth0 (no accounts needed).

## For developers

```powershell
dotnet test  apps/windows/tests/SlothBoss.Core.Tests        # 242 tests (8 live ones run only when opted in)
powershell -File scripts/test-windows.ps1 -LiveModel gemma4:e2b-it-qat
powershell -File scripts/package-windows.ps1                # self-contained zip
SlothBoss.exe --selftest-online report.txt                  # one real call to each partner service with saved keys
```
C# / .NET 10 / WinUI 3 (native Windows UI, no web view), SQLite, MIT license.
Docs: [Install and run](docs/INSTALL.md) · [How the agent works](docs/AGENT.md) · [Architecture](docs/ARCHITECTURE.md) · [Testing](docs/TESTING.md) ·
[Demo script](docs/DEMO_SCRIPT.md) · [Local AI setup](docs/LOCAL_AI_SETUP.md)

## Known limitations

- Windows only for now. Unsigned build.
- Small local models make mistakes; SlothBoss validates every step, but summaries and reports can still be imperfect — check important facts in the linked sources.
- Scanned PDFs (no text layer) aren't read yet, and pictures embedded *inside* slides or documents aren't looked at — save them as image files instead. Old `.doc`/`.ppt`/`.xls` formats aren't read. Up to 6 images are looked at per organizing job (each one takes a few seconds on a laptop). Folder organizing works on the top level of the folder.
- Free tiers of SerpApi, Browserbase and ElevenLabs are limited.
- Web research and voice are tested against each service's documented request/response formats; live
  calls depend on your keys — use the **Test** buttons in Settings (or `--selftest-online`) to confirm.

## License

MIT — see [LICENSE](LICENSE). Gemma is provided under Google's [Gemma Terms of Use](https://ai.google.dev/gemma/terms); SlothBoss doesn't redistribute any model. SerpApi, Browserbase and ElevenLabs are third-party services with their own terms.
