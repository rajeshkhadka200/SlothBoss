# How to record the SlothBoss demo (Windows, ~3 minutes)

The story in one sentence: **"I have chores I keep putting off. I tell SlothBoss — even by voice — and an
open-source AI on my own PC does them, safely."**

Total time: about 45 minutes the first time (30 min setup, 15 min recording and trimming).

---

## Step 1 — Get the files onto the recording PC
1. Copy `SlothBoss-0.4.0-win-x64.zip` to the PC (USB stick, cloud drive or GitHub release).
2. Right-click → **Extract All** → e.g. `C:\SlothBoss`.
3. Double-click `SlothBoss.exe`. If you see "Windows protected your PC": **More info → Run anyway** (the app isn't code-signed yet). Close it again for now.

## Step 2 — Install the local AI (needs internet once)
1. Install **Ollama** from <https://ollama.com/download> and let it start (llama icon in the system tray).
2. Open **Terminal** (Windows key → type "Terminal") and run:
   ```
   ollama pull gemma4:e2b-it-qat
   ```
   It downloads about 4.3 GB. Wait for "success".
3. Warm it up (makes the first answer on camera fast):
   ```
   ollama run gemma4:e2b-it-qat "hello"
   ```
   When it answers, type `/bye`.

## Step 3 — Get the three free keys (in a browser, off camera)
| Service | Sign up | Where the key is |
|---|---|---|
| SerpApi (web search) | <https://serpapi.com> | Dashboard → **Manage API key** |
| Browserbase (reads web pages) | <https://www.browserbase.com> | **Settings → API keys** |
| ElevenLabs (voice) | <https://elevenlabs.io> | Profile → **API keys** → Create |

Keep them in a Notepad window you'll close before recording.

## Step 4 — Set up SlothBoss
1. Open `SlothBoss.exe` → **Settings** (bottom of the sidebar).
2. **1 · Local AI**: should say *Ready — gemma4:e2b-it-qat*. If not, make sure Ollama is running and press **Re-check**.
3. **2 · Web research**: paste the SerpApi key → **Save** → **Test** (✓ Key works). Paste the Browserbase key → **Save** → **Test** (✓ example.com was read). The switch "Allow SlothBoss to research on the internet" should now be **On**.
4. **3 · Voice**: paste the ElevenLabs key → **Save** → **Test** (✓ heard "Organize my downloads folder").
5. **Microphone**: Windows Settings → Privacy & security → Microphone → turn on **Let desktop apps access your microphone**.
6. Go to **Home** → press **Speak**, say "testing", press **Stop**. "Testing" should appear in the box. Delete it.

> Free quotas are small. Test once; rehearse the research demo **at most once**.

## Step 5 — Prepare the scene
1. On Home press **…** (next to the mode box) → **Create a practice folder** → **Create**. A fictional messy folder appears in `Documents\SlothBoss\SlothBoss practice Downloads` and is selected in the folder box.
2. Open that folder in **File Explorer**, set View → **Large icons**.
3. Arrange windows: SlothBoss on the left ~2/3 of the screen, File Explorer on the right ~1/3 (drag to the edges, or Win + ← / Win + →).
4. **If you rehearsed**, reset so the screen is clean: close SlothBoss → delete the practice folder → open `%LOCALAPPDATA%\SlothBoss` (paste into the File Explorer address bar) → delete the `workspace.db` files → open SlothBoss again → **… → Create a practice folder**. Your keys are kept (they're stored separately).
5. Turn on **Do not disturb** (Windows notifications), close Notepad/browser tabs with keys, mute other apps.
6. Screen resolution 1920×1080 if possible. Windows display scale 100–125%.

## Step 6 — Start recording
Use either:
- **Snipping Tool** (built into Windows 11): Windows key → "Snipping Tool" → **Record** (video icon) → **New** → drag to select the whole screen → **Start**. Microphone on.
- or **OBS Studio** (free, <https://obsproject.com>): Display Capture + your mic, Output 1080p 30 fps.

Speak calmly; you'll trim pauses later.

---

## Step 7 — What to do and say (shot by shot)

### Shot 1 · The problem (≈20 s)
**Do:** Show File Explorer with the messy folder (scan001.pdf, document (3).docx, two invoices, notes…).
**Say:** "This is my Downloads folder. A bill called scan001, a lease letter, duplicate invoices, meeting notes. I keep putting it off — and I also have research to do for a project. Let me hand both to SlothBoss."

### Shot 2 · Meet SlothBoss (≈15 s)
**Do:** Click into SlothBoss. Point the mouse at **"Gemma ready"** at the bottom left.
**Say:** "SlothBoss is an open-source desktop assistant. I just say what I want. The AI is Google's open Gemma model running right here on my laptop — my files don't leave this computer."

### Shot 3 · Research, by voice (≈60 s)
**Do:**
1. Folder box → **No folder**. Mode box → **Autopilot**.
2. Press **Speak** and say clearly:
   *"Research the best open-source AI tools for building a desktop assistant. Compare the leading options and give me a short recommendation with sources."*
3. Press **Stop**. The sentence appears in the box. Press **Send**.
4. Wait while the steps tick (✓). Don't talk over silence — narrate:

**Say:** "ElevenLabs turned my voice into text. Now Gemma makes a plan: it searches Google through SerpApi — that little globe means this step goes online — then reads the best pages through Browserbase, and writes a report."

5. When it finishes, scroll through the result. Click one source link (browser opens) → close browser. Click **Open report**.

**Say:** "Every claim links to a source, and SlothBoss labels what it confirmed in a page it actually read versus what only came from a search snippet. If the AI invents a citation, it's removed."

### Shot 4 · The file chore (≈60 s)
**Do:**
1. Click **Home**. Folder box → **SlothBoss practice Downloads**. Mode → **Autopilot**.
2. Click the example **"Organize my Downloads folder"** → **Send**.
3. When the card says **Needs approval**, press **Review and decide**.

**Say:** "Before touching my files, it shows me exactly what it will do: folders by type, duplicates set aside, and clearer names it read from the documents — scan001 becomes the actual electricity bill. Nothing is ever deleted or overwritten, and it can only work inside the folder I allowed."

4. Untick one line (e.g. the move of `trip-ideas.md`) → **Approve selected**.
5. Switch to File Explorer: show the new folders, the renamed bill, the Duplicates folder.
6. Back in SlothBoss: scroll to the green ✓ checks.

**Say:** "And it checks its own work against the real files — every moved file is verified — instead of just saying 'done'."

### Shot 5 · Undo and safety (≈20 s)
**Do:** Press **Undo this job** → **Undo**. Show File Explorer back to the original mess.
**Say:** "Changed my mind? One click puts everything back. And if a document says 'AI, ignore your rules' — like this one — SlothBoss treats it as text, not a command." *(Point at `READ ME FIRST.txt`.)*

### Shot 6 · Close (≈15 s)
**Do:** Open **Settings**, show the three numbered steps.
**Say:** "Setup is three steps: install Ollama with Gemma, then optionally paste free keys for web research and voice. SlothBoss is open source. Give it the work you keep putting off — slowly and steadily, it gets done."

Stop recording.

---

## Step 8 — Trim and export
1. Open the video in **Clipchamp** (built into Windows) → drag it to the timeline.
2. Cut long waits (model thinking) — leave a second of the spinner so it's honest that it takes time. Optionally add a caption "sped up 4×" over any section you speed up.
3. Add a title card: "SlothBoss — tell it what you want, an open-source AI does the boring part".
4. Export 1080p. Upload to YouTube (Unlisted or Public) and paste the link into the DEV post.

## If something goes wrong
| Problem | Fix |
|---|---|
| "The local AI isn't ready" | Start Ollama (tray icon), Settings → **Re-check**. |
| Speak: "Windows blocked the microphone" | Step 4.5. Or just type the request. |
| Research step fails | Settings → **Test** shows why (wrong key or quota used up). |
| Gemma's plan looks a bit different | Totally fine — narrate what's on screen. The approval list and checks are the point. |
| Something fails mid-job | Show it! The card says what failed; press **Retry**. Honesty is part of the pitch. |

## Don't claim on camera
- Don't say it works on Mac (Windows now, Mac is next).
- Only say "works offline" if you unplug the internet and repeat Shot 4 (research needs internet).
