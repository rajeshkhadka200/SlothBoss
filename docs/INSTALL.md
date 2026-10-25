# Install and run SlothBoss (Windows)

About 15 minutes the first time, most of it waiting for downloads.

## What you need

- Windows 10 or 11, 64-bit
- 8 GB of memory (16 GB is more comfortable)
- About 6 GB of free disk space (the app is ~250 MB, the AI model ~4.3 GB)
- An internet connection for the downloads. After that, organizing and reading your files works on
  your computer; only web research and voice use the internet.

## 1. Download SlothBoss

1. Open the [latest release](https://github.com/rajeshkhadka200/SlothBoss/releases/latest).
2. Under **Assets**, click **`SlothBoss-<version>-win-x64.zip`**.
3. In your Downloads folder, right-click the zip → **Extract All…** → choose a place such as
   `C:\SlothBoss` → **Extract**. (Don't run it from inside the zip.)
4. Open the extracted folder and double-click **`SlothBoss.exe`**.

   The build isn't code-signed yet, so Windows may say *"Windows protected your PC"*. Click
   **More info → Run anyway**. You only need to do this once.

No installer, no .NET, nothing else. To remove SlothBoss later, delete that folder (and
`%LOCALAPPDATA%\SlothBoss` if you also want to remove your notes and settings).

## 2. Install the local AI (Gemma)

SlothBoss's brain is Google's open **Gemma** model, running on your own computer through **Ollama**.

1. Download and install **Ollama** from [ollama.com/download](https://ollama.com/download). It starts
   by itself and sits in the system tray.
2. Open **Terminal** (right-click Start → Terminal) and run:
   ```
   ollama pull gemma4:e2b-it-qat
   ```
   This downloads about 4.3 GB once.
3. Back in SlothBoss, the bottom-left corner should say **Gemma ready**. If it doesn't, open
   **Settings → 1 · Local AI** and press **Re-check**. If several Gemma models are installed, pick
   `gemma4:e2b-it-qat` in the **Gemma model** box (it can also look at images).

That's enough for everything except web research and voice.

## 3. Optional: web research and voice

All three services have free plans. Paste each key into **Settings**, press **Save**, then **Test** —
you should see a green tick. Keys are encrypted on your computer (Windows DPAPI) and only ever sent
to their own service.

| Feature | Get a key | Where in Settings |
|---|---|---|
| Web search | [serpapi.com](https://serpapi.com) → sign up → **Manage API key** | 2 · Web research → SerpApi key |
| Reading web pages | [browserbase.com](https://www.browserbase.com) → sign up → **Settings → API key** | 2 · Web research → Browserbase key |
| Voice commands | [elevenlabs.io](https://elevenlabs.io) → sign up → profile → **API keys** | 3 · Voice → ElevenLabs API key |

- Then switch on **Allow SlothBoss to research on the internet** (it is off by default).
- For voice, allow microphone access: Windows **Settings → Privacy & security → Microphone →
  Let desktop apps access your microphone**.

## 4. Your first job

1. Go to **Home**.
2. Want to try safely first? Click **… → Create a practice folder**. It makes a messy folder of
   fictional files and selects it.
   Or click **Folder…** and choose one of your own folders, then **Approve folder**. SlothBoss only
   ever works inside folders you approve.
3. Choose how careful it should be:
   - **Ask me first** — asks before every change
   - **Autopilot** — reads, researches and creates tasks on its own, asks before moving files
   - **Approve plan once** — you approve the whole plan up front
4. Type (or press **Speak** and say) what you want, then **Send**. For example:
   - *Organize this folder and set duplicate copies aside.*
   - *Summarize my slides, spreadsheets and pictures and list what I need to do.*
   - *Research the best open-source tools for X and recommend one with sources.* (needs step 3)
5. When it asks, review the list — untick anything you don't want — and approve.
6. Changed your mind? **Jobs** → open the job → **Undo this job**.

## What it reads

PDF (with a text layer), Word (.docx), PowerPoint (.pptx, including speaker notes), Excel (.xlsx),
text, Markdown, CSV, and images (JPG, PNG, WEBP, GIF, BMP — Gemma looks at them and reads the text
in them). Scanned PDFs and old .doc/.ppt/.xls files aren't read yet.

## Where your data lives

- Notes, tasks, reports and job history: `%LOCALAPPDATA%\SlothBoss\workspace.db` (one file; back it
  up from **Settings → Advanced settings**).
- API keys: `%LOCALAPPDATA%\SlothBoss\secrets.bin`, encrypted for your Windows account.
- Your files stay where they are. SlothBoss never deletes or overwrites a file, and every move can be undone.

## Troubleshooting

| You see | Do this |
|---|---|
| "The local AI isn't ready" | Make sure Ollama is running (tray icon), then Settings → **Re-check**. |
| "No Gemma models found" | Run `ollama pull gemma4:e2b-it-qat` in Terminal, then **Re-check**. |
| Images are skipped: "the selected model can't look at images" | Choose `gemma4:e2b-it-qat` in Settings → Gemma model. |
| A step is slow | The first answer after starting takes ~20 seconds while the model loads; later ones are faster. |
| A web or voice **Test** fails | The message says why — usually a mistyped key or a used-up free quota. |
| **Speak** does nothing | Allow microphone access for desktop apps (step 3). You can always type instead. |
| A file "is in use" | Close it in the other program and press **Retry failed steps**. |
