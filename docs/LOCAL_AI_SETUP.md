# Local AI setup (Ollama + Gemma)

SlothBoss's AI features run entirely on your computer through [Ollama](https://ollama.com). SlothBoss
never downloads a model, never contacts a hosted AI service and has no fallback to one. Everything except
AI works without Ollama.

## 1. Install Ollama
Download it from <https://ollama.com/download> (Windows, macOS) and start it. It serves a local API at
`http://127.0.0.1:11434`.

## 2. Download a Gemma model (one time, needs internet)
```sh
ollama pull gemma4:e2b-it-qat
```
This is the model SlothBoss was developed and tested with: Gemma 4 E2B instruction-tuned, quantization-aware
trained, ~4.6B parameters, Q4_0, **about 4.3 GB on disk**. It ran on an 8 GB RAM Windows laptop on CPU:
the first request after loading took ~20 s; later requests took roughly 3–15 s in our testing. Larger
Gemma variants answer better but need more memory and are slower without a GPU.

Any model Ollama reports in the Gemma family can be selected. Other model families are listed as ignored.

## 3. Select it in SlothBoss
Open **Settings → Local AI**. If exactly one Gemma model is installed it is selected automatically;
otherwise pick one. The sidebar dot turns green ("Gemma ready").

| Setting | Default | Notes |
|---|---|---|
| Endpoint | `http://127.0.0.1:11434/` | must be `localhost` or a loopback IP; anything else is refused |
| Timeout | 180 s | per request, 15–1800 s |

## What runs offline
After the model is downloaded, all features — editing, search, tasks, backups and every AI feature — use
only the local disk and the local Ollama server. Internet is needed only to install Ollama and pull the
model. See [TESTING.md](TESTING.md#offline--privacy) for exactly what was verified.

## Troubleshooting
| Status | Meaning | Fix |
|---|---|---|
| "Ollama is not running" | nothing answered at the endpoint | start Ollama; check the endpoint |
| "No Gemma model is installed" | Ollama runs but has no Gemma model | `ollama pull gemma4:e2b-it-qat` |
| "Choose a Gemma model" | several are installed | pick one in Settings |
| "did not respond within N seconds" | slow machine or long request | raise the timeout; close other apps |
| "model … not found" | the selected model was removed | pull it again or choose another |

## Model terms
Gemma models are provided by Google under the [Gemma Terms of Use](https://ai.google.dev/gemma/terms).
Those terms apply to the model you download and are separate from SlothBoss's MIT license. SlothBoss does
not distribute any model.

## How the agent uses the model

Agent jobs use the selected Gemma model to plan (choose tools and options), replan after failures, group
documents by topic, suggest clearer file names, choose important documents, summarize and extract action
items. Every reply is validated against a JSON schema and the tool registry before anything runs. If the
model a job was planned with becomes unavailable, the job stops with an explanation; SlothBoss never
switches to another model or provider. Without a model you can run folder tidying with the **Rules (no AI)**
planner, which is labelled as such. Details: [AGENT.md](AGENT.md).

## How the notes assistant uses the model
- **Ask**: answers from retrieved excerpts with `[S#]` citations validated by the app.
- **Summarize page**: plain bullet summary; can be appended to the page only after approval.
- **Extract tasks**: proposes tasks with a supporting quote; the app checks the quote exists on the page.
- **Plan tasks**: reviews retrieved notes for unresolved questions; each proposed task is linked to the
  source page it cites.
- **Draft note**: proposes a new page with linked sources.

Thinking output is disabled for models that support it (`think: false`) to keep responses fast.
