# Running a local model (llama.cpp / OpenAI-compatible)

Persistence can drive any OpenAI **Chat Completions**-compatible server (llama.cpp, Ollama, LM Studio,
vLLM) via `Provider = OpenAiChat` + `ApiBaseUrl`. This captures the setup and the performance findings
from running **Qwen3.5-9B (Q6_K_L)** locally on a GTX 1080 Ti (11 GB, Vulkan backend).

## llama-server launch recipe

```
llama-server -m <model>.gguf \
  -ngl 99 \            # offload all layers to GPU
  -c 32768 \           # context window (see sizing below)
  --parallel 1 \       # ONE slot — critical for a single conversation (see findings)
  --jinja \            # use the model's chat template (needed for thinking control)
  --reasoning-budget 0 \  # disable "thinking" for reasoning models (see findings)
  --host 127.0.0.1 --port 8080
```

## Persistence config (env overrides or persistence.json)

```
PERSISTENCE_PROVIDER=OpenAiChat
PERSISTENCE_APIBASEURL=http://127.0.0.1:8080/v1
PERSISTENCE_MODEL=<anything; llama.cpp ignores it>
PERSISTENCE_RESPONSEFORMAT=Tagged
PERSISTENCE_REQUESTTIMEOUTSECONDS=600   # local prompt-ingest can be slow; don't let the client cancel
```

## Performance findings (2026-06, Qwen3.5-9B on a 1080 Ti)

- **`--parallel 1` is critical (~5× faster).** Default `--parallel auto` made 4 slots that *split* the
  context (8192 / 4 ≈ 2048 tokens each). A normal conversation overflows 2048, triggering constant
  context-shift thrashing (~47 tok/s prompt eval, full re-process). One slot gets the full window and
  jumps to ~246 tok/s. For a single peer, always use one slot.
- **KV-cache reuse doesn't work for this model — it's hybrid/recurrent.** llama logs *"forcing full
  prompt re-processing due to lack of cache data (likely due to SWA or hybrid/recurrent memory)"*.
  Qwen3.5 mixes attention with recurrent layers, so llama.cpp can't reuse the prefix across turns — it
  re-ingests the whole prompt every call (~24s for 6k tokens at 246 tok/s). This is architectural, not
  ours. Pure-attention models *can* reuse the prefix; consider that if turn latency matters more than
  this model's quality.
- **Therefore: prompt size drives turn latency directly.** Every turn pays full re-ingest, so keeping
  the working context lean (curation/summary) is the highest-leverage speed lever — more than the
  window size. This is the case for the planned automatic forget/summarize (TODO Tier 2).
- **Thinking models need `--reasoning-budget 0`.** Qwen3.5 otherwise dumps long chain-of-thought into a
  separate `reasoning_content` field, leaving `content` empty until it finishes — our tagged format
  already has a `<think>` tag, so native thinking is redundant here and very slow.
- **Strict chat templates need a single leading system message.** Qwen's Jinja template rejects system
  messages after the conversation; `OpenAiChatModelClient` already flattens to one system + one user
  message to satisfy this.

## Context sizing

- The model is trained for **262,144 tokens**; 8k–64k is well within native range (no RoPE/YaRN
  tricks needed).
- KV cache is cheap here (only 8 attention layers): ~**256 MB at 8k**, scaling ~linearly (~1 GB at 32k,
  ~2 GB at 64k). With a 6.7 GB model on 11 GB, 32k is comfortable.
- Bigger window = more continuity headroom (less truncation), **not** more speed — a fuller prompt is
  slower to re-ingest each turn. Pair larger windows with curation.
