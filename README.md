# KvindoCode

A Claude Code–style coding agent as a native desktop app, built with [Avalonia UI](https://avaloniaui.net/) (.NET 9) and powered by the models of [plusvibeapi.ru](https://plusvibeapi.ru/) (OpenAI-compatible `/v1/chat/completions`).

> **Renamed from PvCode (2026-10-08).** The product, its assemblies (`kvindocode.dll`), its launcher (`kvindocode`),
> its config directory (`~/.kvindocode`) and its environment variables (`KVINDOCODE_*`) all carry the new name. Your
> existing data is not lost: the first start **copies** `~/.pvcode` to `~/.kvindocode` (a copy, not a move, so a build
> still running from the old path keeps working), a leftover `~/.pvcode` is still read as a fallback, the old
> `PVCODE_HOME` is still honoured, and sessions written before the rename keep their model, mode and audit settings
> (the old per-session `pv*` keys are read alongside the new `kv*` ones).

## Install / run
```bash
./install.sh          # builds, installs to ~/.local/{bin,share}, adds a desktop launcher
kvindocode                          # open the current directory as a project
kvindocode ~/dev/app                # open a specific project
kvindocode --last                   # reopen the last project (what the desktop launcher does)
kvindocode -p "fix the failing test" --cwd ~/dev/app [--plan --approve-plan] [--model ID]   # headless, one turn
```
First run: *Settings* (bottom-left) → API key (`~/.kvindocode/settings.json`, mode 600; `KVINDOCODE_API_KEY` overrides).

## Features
| Area | Behaviour |
|---|---|
| **Projects / sessions** | One folder = one project. With `claudeSessionsDir` set (Settings → Sessions) KvindoCode **shares Claude desktop's store**: the registry `…/claude-code-sessions/<account>/<org>/local_*.json` (title, cwd, model, effort) + transcripts in `~/.claude/projects/<cwd>/<id>.jsonl` (Anthropic-format message trees). All your Claude projects/sessions show up; sessions created here show up in Claude. Without it, KvindoCode uses `~/.kvindocode/projects`. |
| **Instructions / skills / memory** | `KVINDOCODE.md`/`CLAUDE.md` chain with `@imports`; `SKILL.md` skills via the `Skill` tool; per-project auto-memory (`MEMORY.md` index) written by the model itself. |
| **Plan mode** (`Shift+Tab`) | Read-only research → **plan review gate** (port of your `plan_review_gate.py` hook: a cheap read-only reviewer model attacks the plan with the exact "you HATE this implementation" prompt, 2 rounds, the agent must revise; +1..3 rounds after you reject; fails open if the reviewer breaks, and **each round has a wall-clock limit** (`PlanReviewTimeoutSeconds`, default 900; if a round hits it the plan is presented with a notice instead of waiting — without it a round could run for about two hours, since it allows nine model calls with retries) → your approval → regular mode. Review rounds are shown as cards, and the plan card has no Approve button while a round is running. |
| **Regular mode** | All tools, no permission prompts (= bypass). |
| **Model picker** | Click the model name: tagged models appear first; the first column is tags, followed by model, separate input/output price (₽/1M), context, max output, release date, TTFT/throughput/uptime, vision/tools/effort; filter + sort on every column; per-model provider routes; **public benchmark scores** per topic (coding, legal, security, safeguards) as average leaderboard position, with per-benchmark breakdown (values, ranks, sources) and an *Update scores* button — scores come from `Assets/scores.json` via `ScoreStore`, read with `JsonText`, and `ScoreStoreTests` loads the real bundled file so a parse regression cannot silently empty the list again. |
| **Effort** | Composer dropdown (auto/low/medium/high/xhigh/max, per model's supported levels), stored per session (`effort` in the registry) and sent as `reasoning_effort`. |
| **Search** | Sidebar box (`Ctrl+F`): case-insensitive **regex** over session titles *and the text inside sessions* (user/assistant text, tool calls, tool-result heads), with snippets. Backed by a ~46 MB index built in seconds (`~/.kvindocode/search-index`), refreshed when transcripts change. |
| **Titles** | The session actions sit **beside the title**: 📋 copy the name to the clipboard, ✎ rename (or right-click), ✨ regenerate with AI (title model, default Haiku), 📌 pin. New sessions are auto-titled after the first turn. |
| **Background tasks** | `Monitor` (stream a command's output lines into the chat — e.g. "write hello every 10 seconds"), `Bash run_in_background`, `TaskOutput/TaskStop/TaskList`; Tasks panel with Stop; the model is woken when a task exits (or on output with `wake_on_output`). |
| **Browser** | `Browser` tool drives Chrome over DevTools (tabs, navigate, read page with numbered elements, click/type/select/keys/scroll, screenshots shown inline, JS, dialogs). Attaches to a Chrome started with `--remote-debugging-port` (or finds its `DevToolsActivePort`); otherwise launches its own Chrome with a persistent profile (`~/.kvindocode/chrome-profile`) — Chrome cannot attach to an already-running instance started without the flag. |
| **VPN bypass** | When a VPN interface (tun/wg/ppp…) is up, API sockets are pinned to the physical default-route interface (`SO_BINDTODEVICE`, no root, no route changes). Settings shows the live status. |
| **Context** | Auto-compaction at 90 % of the window (works on huge imported sessions); usage + gateway cost in the top bar, with the last request's cache hit and the session average. |
| **Read tool** | Text, images (shown to vision models), PDFs (`pages`), docx/xlsx/pptx, notebooks. |
| **Attachments** | Drag & drop files, `Ctrl+V` (clipboard image or copied files) or the 📎 button. Images go to vision models as image parts (the model picker's *Input* column / "Accepts" line shows each model's supported formats); other files are referenced by path and read with the Read tool. |
| **Secrets vault** | Sidebar → *Secrets*: AES-256-GCM encrypted values with SHA-256 metadata. Detection is **deterministic first**: PEM / OpenSSH blocks, kubeconfig key-data, JWTs, known token prefixes (GitHub, GitLab, Slack, Stripe, AWS, Google, Telegram, …), credentials in URLs and values of secret-named keys (`token:`, `password=`, …) are found by patterns, offline, with no model — our own rules plus the embedded **gitleaks** default ruleset (MIT, ~220 vendor formats; regenerate with `tools/update-gitleaks-rules.py`). Only the **value** is replaced (by a reversible named marker); the key and the rest of the line stay. The local auditor model is a **second opinion** for things the patterns do not recognise: it never masks silently — each candidate goes through a *It is a secret / Not a secret / Cancel* dialog (non-secret stores only a SHA-256 exclusion; cancel withholds the tool output). Images are checked by the model only. If the model is down, pattern detection keeps working. The whole outbound history is scanned on every request, so a credential written into an earlier tool call is caught and registered in the *Leaked* tab for rotation. A corrupt vault or a lost key file is reported at startup instead of looking empty, every save keeps a `.bak`, and, inside the *Secrets* section beside the list, a **filter that shows entries which look like false positives** — a value containing a marker (the "hidden twice" case), a marker fragment, our redaction text, a file path, a variable reference, a multi-line body, or prose with no digits — with a **"Delete all false positives…"** action that asks once for all of them or per entry. It deliberately leaves alone what it cannot tell from a real credential: a long single token (a key blob), a value with digits, and ordinary words (`password` and `mypassword` look identical). Auditing is per session and the switch covers that session's **subagents** too. |
| **Session work mode** | The header icon next to the shield toggles *Delegate*: the session then only answers, asks you and works through subagents — it is not offered Read/Write/Edit/Bash/Browser and such calls are refused. Useful to keep an expensive model to orchestration while a cheap default subagent model does the file and shell work. Per session, persisted. |
| **Rewind / fork** | Hover a user message: ↶ rewinds the conversation to before it (message + attachments go back into the composer; the transcript file keeps the old branch; file edits are not undone), ⑂ forks a new session from that point. |
| **Side panel** | Right-hand pane beside the transcript: **Files** (the project tree — a directory is listed when you expand it, so it costs nothing to show and switching sessions stays instant), plans, tool details, file previews, subagents, and project context. Clicking a path in the transcript opens its preview and selects it in the tree; selecting a file in the tree opens it. Context rows open in this pane; skill and memory rows have context-menu editing with Save. |
| **Hooks** | Claude Code hooks are honoured: `~/.claude/settings(.local).json`, `<project>/.claude/settings(.local).json`, `~/.kvindocode/hooks.json`, `<project>/.kvindocode/hooks.json` (UserPromptSubmit, PreToolUse, PostToolUse, Stop, Notification; exit 2 / `decision:block`). `plan_review_gate.py` is skipped while the native plan review is on. Toggle in Settings; listed in the Context window. |
| **Sidebar** | *Pinned* and project groups are collapsed by default; a dot shows a working session (pulsing) or one with running background tasks, also on collapsed group headers. A session that finished in the background gets a blue dot and an entry in **Needs attention**: clicking the dot acknowledges it (the light goes out, the entry stays), the block's ✓ clears the dot and its ✕ removes the entry, and the block header's ✕ removes all of them. Writing to a session never clears its entry on its own — only another finished turn makes it wait again. |
| **Continue after a restart** | At startup KvindoCode offers to continue the sessions that were active when it last was alive (`LastRunEnded`, refreshed every 20 s; a session counts if its newest message is within 15 min of it — `AppSettings.WasActiveInPreviousRun`). Continuing sends that session a real user turn: *“The app was restarted, please continue where you left off. Note that background tasks and loops will not be autocontinued, so you have to restore them yourself.”* The dialog offers *Continue all / Choose individually / Skip all*. |
| **UI** | Left-aligned transcript, collapsible tool cards with diffs, closable task list, light/dark theme. |

The header has a **find-in-session** box (case-insensitive regexp, literal if invalid): Enter / Shift+Enter step through the matches, which are real selections and can be copied; Esc clears it. The **Notification** hook payload carries `session_title`, `session_id` and `project`, so a desktop notification can name the session.

Keys: `Enter` send · `Shift+Enter` newline · `Shift+Tab` plan/regular · `Esc` stop · `Ctrl+N` new session · `Ctrl+O` open folder · `Ctrl+F` search · `Ctrl+,` settings.

## Develop
```bash
dotnet test tests/KvindoCode.Tests      # 723 tests
python3 tools/update_scores.py      # refresh src/KvindoCode.App/Assets/scores.json
dotnet run --project src/KvindoCode.App -- /path/to/project
```
`KVINDOCODE_SCRIPT=file.json` swaps the API for a scripted fake (UI tests/demos); `KVINDOCODE_HOME` relocates `~/.kvindocode`.
