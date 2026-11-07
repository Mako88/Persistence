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

## Persistence config

Add a model profile to `persistence.json` and point `SelectedModel` at it (profiles are described in
the [README](../README.md#configuration)):

```jsonc
{
  "SelectedModel": "local",
  "Models": [
    {
      "Name": "local",
      "Provider": "OpenAiChat",
      "Model": "local",                         // llama.cpp ignores it
      "ApiBaseUrl": "http://127.0.0.1:8080/v1",
      "MaxInputTokens": 28000,                  // ≤ the server's -c, minus output headroom
      "MaxOutputTokens": 4096,
      "Streaming": false,
      "RequestTimeoutSeconds": 600              // local prompt-ingest is slow; don't cancel early
    }
  ]
}
```

Or override per-run without editing the file (model-coupled vars apply to the active profile):

```
PERSISTENCE_SELECTEDMODEL=local
PERSISTENCE_PROVIDER=OpenAiChat
PERSISTENCE_APIBASEURL=http://127.0.0.1:8080/v1
PERSISTENCE_REQUESTTIMEOUTSECONDS=600
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

## Gemma 12B (gemma-4-12B-it, Q4_K_M) findings — 2026-06, same 1080 Ti

Verified end-to-end with Persistence (two-way connection, multi-turn). Launch recipe:

```
llama-server -m gemma-4-12B-it-q4_k_m.gguf ^
  -ngl 99 -c 32768 --parallel 1 --jinja --reasoning-budget 0 -t 4 ^
  --host 127.0.0.1 --port 8080
```

- **It's a thinking model → `--reasoning-budget 0` is mandatory.** Without it, the reply lands in
  `reasoning_content` and `message.content` is **empty** — `OpenAiChatModelClient` reads `content`, so
  every turn would fail to parse. With it disabled, content comes through cleanly. (Same lesson as
  Qwen, but here the symptom is empty replies, not just slowness.)
- **System role is accepted.** Unlike Qwen's stricter template, llama.cpp's Gemma `--jinja` path takes
  the one-system-plus-one-user shape `OpenAiChatModelClient` already sends — no template error.
- **Vulkan shader warm-up dominates the first call** (~8 tok/s), then settles to **~85 tok/s prompt
  eval, ~30 tok/s generation**. Do a throwaway warm-up request before timing anything.
- **KV cache is cheap thanks to sliding-window attention (SWA).** At 32k: **992 MiB** total — 512 MiB
  global (scales ~16 KiB/token) + 480 MiB sliding-window (**fixed** regardless of context). So the
  global part is the only thing that grows; 8k→32k cost only +384 MiB.
- **VRAM is the real ceiling, not system RAM.** Fully offloaded (`-ngl 99`), weights (7.4 GB) + KV +
  compute live in VRAM, which on a single-GPU desktop is **shared with Windows**. 32k leaves ~1.7 GB
  free for desktop spikes; 64k is reachable only if you keep other GPU apps minimal. Freeing system
  RAM doesn't raise the context ceiling for a GPU-resident model (it only helps load/host buffers).
- **Sizing `MaxInputTokens`:** keep it below the server's `-c` minus generation headroom. For `-c
  32768` we use `MaxInputTokens=28000` + `MaxOutputTokens=4096` (the client sends the latter as
  `max_tokens`; the two must fit in one window).
- **Schema-following quirk:** the 12B sometimes wrote `type=`/`tag=` instead of `fragment_type=`/
  `tags=`; the command layer's unknown-field "did you mean?" hints caught it gracefully (fragment still
  added, just untyped/untagged). A sharper model follows the command schema more reliably.

## Running the embedding server (semantic recall)

Semantic associative recall (`Embedding.*` in config) needs an OpenAI-compatible `/embeddings`
endpoint. That is a **second** `llama-server` with its own small model — not the chat model. Two
reasons it is separate rather than reusing the peer's model:

- A generation model's hidden states make poor embeddings; a purpose-trained embedding model is far
  better and ~50× smaller.
- Every peer can share one embedding server regardless of which provider it thinks with, so an
  Anthropic peer and a local peer produce comparable vectors.

```
llama-server -m nomic-embed-text-v2.gguf ^
  --embedding --pooling mean ^
  -ngl 0 -c 8192 --parallel 1 ^
  --host 0.0.0.0 --port 8081
```

- **`-ngl 0` (CPU) is the right default on this box.** VRAM is the binding constraint — a 12B Q4 chat
  model plus its KV cache already leaves ~1.7 GB free at 32k context. Embedding a handful of short
  texts is milliseconds on CPU, so spending VRAM on it buys nothing and risks evicting the chat model.
- **`--embedding` is required**, and `--pooling mean` matches what most embedding models expect. Without
  `--embedding`, the endpoint 404s and recall silently stays lexical (which is the designed fallback, so
  the only symptom is that nothing improves).
- **A separate port from the chat server** (8081 vs 8080). Both can run at once; only one holds VRAM.

### Verified live (2026-09-03, gemma-4-12B as a stand-in embedder on CPU)

Run against a real `llama-server --embedding --pooling mean` to check the wire format rather than the
spec, using the chat model because no dedicated embedding model was on the box:

- **Response shape confirmed:** `{data:[{object, index, embedding}], model, usage}`, `embedding` a flat
  array of floats. 3840 dimensions for the 12B. `index` is present and is what ordering must key on.
- **`--pooling mean` matters more than it looks.** Without it, `embedding` comes back as one vector
  *per token* — a nested array. The client mean-pools that case itself rather than rejecting it,
  because the rejection path is a silent fall back to lexical, i.e. the symptom would be "nothing
  improved" with nothing in the log to explain it.
- **A generation model is a poor embedder, measured:** cosine between two deliberately unrelated
  sentences was **0.785**. The space is anisotropic — everything looks similar to everything. Ranking
  still functions (rank fusion only needs relative order) but discrimination is weak, which is the
  concrete argument for a purpose-trained embedding model rather than reusing the chat model.
- **Cost of the stand-in:** ~7.2 GB resident on CPU, ~2 min to load, ~26 s for a two-text embed call.
  A real embedding model is ~50× smaller and effectively instant.

Config side:

```jsonc
"Embedding": {
  "Enabled": true,
  "ApiBaseUrl": "http://127.0.0.1:8081/v1",   // containers: http://host.docker.internal:8081/v1
  "Model": "nomic-embed-text-v2",             // stored with every vector — see below
  "BackfillPerTurn": 32
}
```

Or per-run, without editing the file: `PERSISTENCE_EMBEDDING_ENABLED`, `PERSISTENCE_EMBEDDING_APIBASEURL`,
`PERSISTENCE_EMBEDDING_MODEL`.

**`Model` is not cosmetic.** It is stored alongside every vector and is part of a vector's identity.
Cosine similarity between two different models' vectors is meaningless rather than merely noisy, so
changing this name leaves the old vectors inert and re-embeds from scratch — which is correct, and is
why switching back reuses what is already there. Changing embedding models therefore costs a warm-up,
not a corruption.

**Warm-up.** Vectors backfill lazily, up to `BackfillPerTurn` fragments per turn, newest first. A store
with a few hundred authored memories is fully embedded after a handful of turns; until then recall is
progressively more semantic and never worse than lexical.
