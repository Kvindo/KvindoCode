---
name: local-model-docker
description: Run a local OpenAI-compatible model in Docker (Ollama or vLLM) and point KvindoCode at it — for offline use, a cheap summariser, or the secret auditor. Use when asked to "run a local model", "use ollama", "host vLLM in docker", "offline model", or to wire `apiBaseUrl` to a local endpoint.
---

# Local model in Docker, wired into KvindoCode

KvindoCode talks to any [OI]-compatible `/v1/chat/completions` endpoint, so a container is enough — the model never
has to leave the machine. Requires Docker and ~5 GB free for a 7B-class model.

## 1. Pick a runtime

| Runtime | Best for | Endpoint |
|---|---|---|
| **Ollama** | fastest to try, manages models + GPU | `http://127.0.0.1:11434/v1` |
| **vLLM** | throughput, exact OpenAI semantics, the auditor | `http://127.0.0.1:8001/v1` |

## 2. Run it (Ollama)

```bash
docker run -d --name ollama --restart unless-stopped \
  -p 127.0.0.1:11434:11434 \
  -v ollama:/root/.ollama \
  ollama/ollama
docker exec -it ollama ollama pull qwen2.5:7b-instruct     # ~4.7 GB
```

With an NVIDIA GPU add `--gpus all` (needs nvidia-container-toolkit). Bind to `127.0.0.1` only — a model endpoint
open on `0.0.0.0` is an unauthenticated API on the network.

## 3. Run it (vLLM)

```bash
docker run -d --name vllm --restart unless-stopped --gpus all \
  -p 127.0.0.1:8001:8000 \
  -v hf-cache:/root/.cache/huggingface \
  vllm/vllm-openai:latest \
  --model Qwen/Qwen2.5-7B-Instruct --max-model-len 16384 --served-model-name local
```

CPU-only vLLM is slow but works: drop `--gpus all` and add `--device cpu`.

## 4. Verify before touching KvindoCode

```bash
curl -s http://127.0.0.1:11434/v1/models | head -c 300     # Ollama
curl -s http://127.0.0.1:8001/v1/models  | head -c 300     # vLLM
curl -s http://127.0.0.1:8001/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"local","messages":[{"role":"user","content":"reply with OK"}]}' | head -c 300
```

Any non-empty `data` array is enough. A connection refused here means the container is not up — check
`docker logs <name>`.

## 5. Point KvindoCode at it

* **Whole session** — Settings → Connection → *API base URL* = `http://127.0.0.1:11434/v1`, *API key* = anything
  (Ollama ignores it), then pick the model from the model picker (its id is the tag you pulled, e.g.
  `qwen2.5:7b-instruct`).
* **Secret auditor** — Settings → Vault & audit → *Auditor URL* `http://127.0.0.1:8001/v1`, *Auditor model* `auditor`
  when vLLM was started with `--served-model-name auditor`. The auditor only sees text/images that are about to be
  sent to a cloud model.
* **One-off** — `kvindocode -p "summarise this log" --cwd /path --model local`.

## 6. Gotchas

* **Context window**: a 7B model at 8k context truncates long tool output. Set `maxTokens`/`--max-model-len`
  explicitly and expect KvindoCode to compact sooner.
* **No tool calls**: a model without tool-call training will answer in prose and never use Read/Bash. Check with the
  curl above before blaming KvindoCode.
* **GPU vs CPU**: on CPU expect seconds per token; keep such a model out of long agent loops.
* **Disk**: models live in the named volume (`ollama`, `hf-cache`) — `docker volume rm` reclaims them, and removing
  the container alone keeps the data.
* **Port collision**: if `11434` is taken, change the left side of `-p` and the Settings URL together.
