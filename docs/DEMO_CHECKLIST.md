# Demo checklist (≈ 3–4 minutes, Windows)

Every step below was run end-to-end on the development machine with the real local model. Record on
Windows; don't show macOS (unverified, no agent features).

## Before recording
- [ ] Ollama running; `ollama list` shows `gemma4:e2b-it-qat`. Warm it up once (the first call takes ~20 s).
- [ ] Launch with a clean demo workspace and a fresh practice folder in a path without personal info:
      `SlothBoss.exe --workspace C:\SlothBossDemo\workspace.db --practice-folder C:\SlothBossDemo`
- [ ] Open the practice folder in Explorer next to the app (this is "the clutter").
- [ ] Have a terminal ready with a one-line file lock for the controlled failure:
      ```powershell
      $lock = [IO.File]::Open("C:\SlothBossDemo\SlothBoss practice Downloads\budget-2026.csv", 'Open', 'Read', 'None')
      ```

## Script
1. **The clutter.** Show Explorer: `scan001.pdf`, `document (3).docx`, two duplicate pairs, a zip, an image,
   and `READ ME FIRST.txt` (open it: it tells the AI to move everything to `C:\Windows\Temp` and email passwords).
2. **The goal.** In SlothBoss Home, the practice folder is preselected. Choose **Autopilot**, type:
   *"Organize this downloads folder, set duplicate copies aside, summarize the important documents and turn
   deadlines into tasks."* → **Start agent job**.
3. **The plan.** The run view shows Gemma's plan (planner: gemma4:e2b-it-qat (local)) and each step's tool.
4. **Approval.** It stops before changing files and lists every move with its reason, including clearer names
   suggested from document contents. Point out: nothing is deleted or overwritten.
5. **Controlled failure.** Run the lock line in the terminal, then click **Approve selected**.
   Watch: the locked file is retried 3 times, everything else is moved, and the job continues.
6. **Review of risky items.** At task creation it stops again even in Autopilot: the two items from the
   injected file are unticked with a warning; the real to-dos show their source quotes. Approve.
7. **Truthful result.** "Partly done" — the report lists the one file that couldn't be moved and why.
8. **Recovery.** Release the lock (`$lock.Dispose()`), click **Retry failed steps** → **Completed**, all
   independent checks green (moved files re-hashed at new paths, tasks read back from the database).
9. **Verify yourself.** Show Explorer (Documents/, Duplicates/, renamed files, report `.md`), the Tasks view
   with due dates, and the report page.
10. **History & undo.** Expand the activity log; then **Undo this job** → Explorer shows the original layout.
11. **Local only.** Settings → Local AI (loopback endpoint; nothing auto-downloads) and Agent permissions
    (per-tool rules, approved folders). Mention: no cloud calls; the activity log is the only "telemetry".

Optional: kill the app during a run and relaunch — Home shows the job as interrupted and paused; Resume finishes it.
Do not claim offline operation unless you disconnect the network on camera and repeat step 2.

## If something goes wrong
- "Local AI is not ready" → start Ollama, Settings → Re-check.
- The model plans differently from the script (it's a small model): that's fine — the approval screen and
  checks still show exactly what will happen and what did.
