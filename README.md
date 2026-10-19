# KvindoCode

**A desktop AI coding agent for Linux, Windows and macOS - a native Avalonia app, not a terminal TUI.** It runs a
full agent loop (files, shell, git worktrees, browser, background tasks, subagents) against any compatible
`/v1/chat/completions` endpoint, and it is built around a *local, encrypted secret vault*: credentials in tool output
and in outbound requests are detected, masked and optionally rotated instead of being pasted into a transcript.

Built with [Avalonia UI](https://avaloniaui.net/) 12 on .NET 10 and powered by the models of
[plusvibeapi.ru](https://plusvibeapi.ru/?ref=DQPPLBH2); any gateway that speaks the same protocol works.

**Why it exists.** Two things are missing from most agent CLIs. *Isolation:* two agents editing one checkout corrupt
each other's index, HEAD and files, so every session here gets its own **git worktree and branch**. *Secret hygiene:*
a `cat .env` or a `psql -c` ends up in the transcript and then in every later request, so an **AES-256-GCM vault**
sits in front of the wire - deterministic detection first, a local model only as a second opinion, reversible markers
for the model, and a leak register for anything that escaped before it was known.

**At a glance** - `git worktree` per session; plan mode with an adversarial plan reviewer; subagents with
discoverable model tags; a real **XTerm grid** (four panes, tmux-backed persistence); file tree and side panel;
find-in-session across the whole history; tokens, cost and cache figures; Telegram (Bot API and MTProto user
session); Chrome automation over DevTools; encrypted vault with leak register and key rotation; per-session secret
auditing that covers subagents.

**Search terms** (what this project is, for people looking for it): AI coding agent, autonomous coding assistant,
desktop GUI coding agent, Avalonia coding agent, C# / .NET AI agent, Linux AI coding agent, GUI alternative to a
terminal coding agent, OpenAI-compatible agent client, local LLM agent GUI, git worktree isolation for coding agents,
agent with secret scanning and masking, encrypted credential vault for an AI agent, embedded XTerm terminal in
Avalonia.

![KvindoCode mark](src/KvindoCode.App/Assets/kvindo-k.png)

> **Renamed from PvCode (2026-10-08).** The product, its assemblies (`kvindocode.dll`), its launcher (`kvindocode`),
> its config directory (`~/.kvindocode`) and its environment variables (`KVINDOCODE_*`) all carry the new name. Your
> existing data is not lost: the first start **copies** `~/.pvcode` to `~/.kvindocode` (a copy, not a move, so a build
> still running from the old path keeps working), a leftover `~/.pvcode` is still read as a fallback, the old
> `PVCODE_HOME` is still honoured, and sessions written before the rename keep their model, mode and audit settings
> (the old per-session `pv*` keys are read alongside the new `kv*` ones).

## Install / run
```bash
./install.sh                        # build from this checkout, install to ~/.local/{bin,share}
./install.sh --from-release [tag]   # install a prebuilt GitHub release instead (no .NET SDK needed)
kvindocode                          # open the current directory as a project
kvindocode ~/dev/app                # open a specific project
kvindocode --last                   # reopen the last project (what the desktop launcher does)
kvindocode -p "fix the failing test" --cwd ~/dev/app [--plan --approve-plan] [--model ID]   # headless, one turn
```
Prebuilt self-contained archives for Linux, macOS (Intel + Apple silicon) and Windows are attached to every release:
<https://github.com/Kvindo/KvindoCode/releases>. `tools/release.sh <version>` builds all of them and creates the
release (`--draft` to check it first, `--no-push` to build the files only).

Both scripts swap the installed files **per file**, so an app that is already running keeps working until you restart
it. Builds are throttled (`-maxcpucount:1`, `nice`, one global lock) so they do not make the desktop unresponsive;
`KVINDOCODE_BUILD_FAST=1` lifts that for a one-off fast build.

First run: *Settings* (bottom-left) → API key (`~/.kvindocode/settings.json`, mode 600; `KVINDOCODE_API_KEY` overrides).

## Features
| Area | Behaviour |
|---|---|
| **Projects / sessions** | One folder = one project. With `claudeSessionsDir` set (Settings → Sessions) KvindoCode **shares Claude desktop's store**: the registry `…/claude-code-sessions/<account>/<org>/local_*.json` (title, cwd, model, effort) + transcripts in `~/.claude/projects/<cwd>/<id>.jsonl` (Anthropic-format message trees). All your Claude projects/sessions show up; sessions created here show up in Claude. Without it, KvindoCode uses `~/.kvindocode/projects`. |
| **Session isolation (git worktrees)** | Several sessions on one project would otherwise share one working tree, `.git/index` and HEAD, so parallel work corrupts itself (`index.lock`, "would be overwritten by checkout", one session's `git add -A` sweeping another's edits). Each session therefore runs in its **own `git worktree`** (`~/.kvindocode/worktrees/<project>/<id>`, branch `kv/<id>`, branched from HEAD) — its own working files and index, the repository's object store shared. **Nothing is committed or merged for you**: the header chip shows the branch and its click copies `git merge kv/<id>`; merge it (or open a PR) when you want the work in the project. The **file tools refuse to write outside the session's worktree** (and refuse to read a *peer* session's worktree); Bash is forced into the worktree and a `cd` out is rejected, but a shell can still reach an absolute path outside — that is not a hard sandbox. A folder that is not a git repo, or has no commit yet, is used directly as before (the chip says "shared tree"). Settings → Sessions: on/off and a per-project setup script (link `node_modules`, `.venv`, …). |
| **Instructions / skills / memory** | `KVINDOCODE.md`/`CLAUDE.md` chain with `@imports`; `SKILL.md` skills via the `Skill` tool, and typing `/` in the composer opens a skill picker popup; per-project auto-memory (`MEMORY.md` index) written by the model itself. |
| **Plan mode** (`Shift+Tab`) | Read-only research → **plan review gate** (port of your `plan_review_gate.py` hook: a cheap read-only reviewer model attacks the plan with the exact "you HATE this implementation" prompt, 2 rounds, the agent must revise; +1..3 rounds after you reject; fails open if the reviewer breaks, and **each round has a wall-clock limit** (`PlanReviewTimeoutSeconds`, default 900; if a round hits it the plan is presented with a notice instead of waiting — without it a round could run for about two hours, since it allows nine model calls with retries) → your approval → regular mode. Review rounds are shown as cards, and the plan card has no Approve button while a round is running. |
| **Regular mode** | All tools, no permission prompts (= bypass). |
| **Model picker** | Click the model name: tagged models appear first; the first column is tags, followed by model, separate input/output price (₽/1M), context, max output, release date, TTFT/throughput/uptime, vision/tools/effort; filter + sort on every column; per-model provider routes; **public benchmark scores** per topic (coding, legal, security, safeguards) as average leaderboard position, with per-benchmark breakdown (values, ranks, sources) and an *Update scores* button — scores come from `Assets/scores.json` via `ScoreStore`, read with `JsonText`, and `ScoreStoreTests` loads the real bundled file so a parse regression cannot silently empty the list again. |
| **Effort** | Composer dropdown (auto/low/medium/high/xhigh/max, per model's supported levels), stored per session (`effort` in the registry) and sent as `reasoning_effort`. |
| **Search** | Sidebar box (`Ctrl+F`): case-insensitive **regex** over session titles *and the text inside sessions* (user/assistant text, tool calls, tool-result heads), with snippets. Backed by a ~46 MB index built in seconds (`~/.kvindocode/search-index`), refreshed when transcripts change. |
| **Titles** | The session actions sit **beside the title**: 📋 copy the name to the clipboard, ✎ rename (or right-click), ✨ regenerate with AI (title model, default Haiku), 📌 pin. New sessions are auto-titled after the first turn. |
| **Background tasks** | `Monitor` (stream a command's output lines into the chat — e.g. "write hello every 10 seconds"), `Bash run_in_background`, `TaskOutput/TaskStop/TaskList`; Tasks panel with Stop, and a direct Stop button on task notice cards in the transcript; the model is woken when a task exits (or on output with `wake_on_output`). |
| **Browser** | `Browser` tool drives Chrome over DevTools (tabs, navigate, read page with numbered elements, click/type/select/keys/scroll, screenshots shown inline, JS, dialogs). Attaches to a Chrome started with `--remote-debugging-port` (or finds its `DevToolsActivePort`); otherwise launches its own Chrome with a persistent profile (`~/.kvindocode/chrome-profile`) — Chrome cannot attach to an already-running instance started without the flag. |
| **Telegram** | Native `Telegram` tool: supports both **Bot API** and **MTProto User API** (`WTelegramClient` with `kvindocode --telegram-login`). A session can **read** what a bot or user received and **send** messages or files (`me`, `read`, `send`, `send_file`/`send_photo`/`send_document`, `edit`, `delete`, `chat`, `admins`). Credentials live in the encrypted vault and only their **names** are configured in Settings, so they are never parameters and never printed; tokens are scrubbed from error messages too. `read` long-polls (`wait`) and remembers the last update id, so it is incremental. Setup recipe: the `telegram-setup` skill. |
| **VPN bypass** | When a VPN interface (tun/wg/ppp…) is up, API sockets are pinned to the physical default-route interface (`SO_BINDTODEVICE`, no root, no route changes). Settings shows the live status. |
| **Context** | Auto-compaction at 90 % of the window (works on huge imported sessions); usage + gateway cost in the top bar, with the last request's cache hit and the session average. |
| **Read tool** | Text, images (shown to vision models), PDFs (`pages`), docx/xlsx/pptx, notebooks. |
| **Attachments** | Drag & drop files, `Ctrl+V` (clipboard image or copied files) or the 📎 button. Images go to vision models as image parts (the model picker's *Input* column / "Accepts" line shows each model's supported formats); other files are referenced by path and read with the Read tool. |
| **Secrets vault** | Sidebar → *Secrets*: AES-256-GCM encrypted values with SHA-256 metadata. Detection is **deterministic first**: PEM / OpenSSH blocks, kubeconfig key-data, JWTs, known token prefixes (GitHub, GitLab, Slack, Stripe, AWS, Google, Telegram, …), credentials in URLs and values of secret-named keys (`token:`, `password=`, …) are found by patterns, offline, with no model — our own rules plus the embedded **gitleaks** default ruleset (MIT, ~220 vendor formats; regenerate with `tools/update-gitleaks-rules.py`). Only the **value** is replaced (by a reversible named marker); the key and the rest of the line stay. The local auditor model is a **second opinion** for things the patterns do not recognise: it never masks silently — each candidate goes through a *It is a secret / Not a secret / Cancel* dialog (non-secret stores only a SHA-256 exclusion; cancel withholds the tool output). Images are checked by the model only, and a finding on a picture is never an automatic drop: the model is the only thing that can read pixels, and on ordinary screenshots it flags structural things (a host name, an address, a ULID, a key name) most of the time, so a quoted value that the deterministic plausibility filter rejects is treated as **not** a secret and the picture is sent with a note. Because everything the whole history holds rides every later request, the same picture is audited once and its verdict remembered. A quote that does look like a credential is shown to the human **with the image** (*Send it anyway / Leave it out*); with nobody to ask (headless) the picture stays out. If the model is down, pattern detection keeps working. The whole outbound history is scanned on every request, so a credential written into an earlier tool call is caught and registered in the *Leaked* tab for rotation. A corrupt vault or a lost key file is reported at startup instead of looking empty, every save keeps a `.bak`, and, inside the *Secrets* section beside the list, a **filter that shows entries which look like false positives** — a value containing a marker (the "hidden twice" case), a marker fragment, our redaction text, a file path, a variable reference, a multi-line body, or prose with no digits — with a **"Delete all false positives…"** action that asks once for all of them or per entry. It deliberately leaves alone what it cannot tell from a real credential: a long single token (a key blob), a value with digits, and ordinary words (`password` and `mypassword` look identical). Auditing is per session and the switch covers that session's **subagents** too. **Rotating the master key** (`kvindocode --rotate-vault-key`, with the app closed): every stored value is re-encrypted under a fresh 32-byte key. It decrypts everything first and aborts without writing if any entry cannot be read, backs up the old key + old vault to a timestamped `key-rotation-…` directory, and verifies the written files with the new key alone before finishing. Values left over from a *previous* key (unreadable non-secret/leak blobs) have their blobs emptied — their hashes and metadata are kept. |
| **Session work mode** | The header icon next to the shield toggles *Delegate*: the session then only answers, asks you and works through subagents — it is not offered Read/Write/Edit/Bash/Browser and such calls are refused. Useful to keep an expensive model to orchestration while a cheap default subagent model does the file and shell work. Per session, persisted. |
| **Rewind / fork** | Hover a user message: ↶ rewinds the conversation to before it (message + attachments go back into the composer; the transcript file keeps the old branch; file edits are not undone), ⑂ forks a new session from that point. |
| **Side panel** | Right-hand pane beside the transcript: **Files** (the project tree — a directory is listed when you expand it, so it costs nothing to show and switching sessions stays instant), plans, tool details, file previews, subagents, and project context. Clicking a path in the transcript opens its preview and selects it in the tree; selecting a file in the tree opens it. Context rows open in this pane; skill and memory rows have context-menu editing with Save. |
| **File manager** | The Files icon in a project header opens a VS Code-like editor: a lazy project tree with name filtering, syntax highlighting (AvaloniaEdit + TextMate), autosave, and new file/folder, rename, delete, copy/paste (`Ctrl+C` / `Ctrl+V` on the tree; a name already in use becomes `name (2).ext`), duplicate, and a right-click context menu. `Ctrl+F` is a find/replace bar over the open file; `Ctrl+Shift+F` searches the whole project with **Replace All**. `F4` toggles a terminal panel at the bottom, with the editor still on screen. |
| **Project notes** | The notes icon in a project header opens one markdown file per project (`<project>/.kvindocode/NOTES.md`) in the side panel, saved as you type. |
| **Hooks** | Claude Code hooks are honoured: `~/.claude/settings(.local).json`, `<project>/.claude/settings(.local).json`, `~/.kvindocode/hooks.json`, `<project>/.kvindocode/hooks.json` (UserPromptSubmit, PreToolUse, PostToolUse, Stop, Notification; exit 2 / `decision:block`). `plan_review_gate.py` is skipped while the native plan review is on. Toggle in Settings; listed in the Context window. |
| **Alerts** | A session that needs you **beeps natively** (your `~/.local/bin/beep`, else `paplay`/`pw-play` with a generated WAV) and shows a **desktop notification** (`notify-send`, else `kdialog`) — no Notification hook needed. **Waiting for you counts too, not only a finished turn**: a question, a plan approval or a secret confirmation alerts as soon as it opens (only for a session you are *not* looking at, coalesced app-wide, so it can never become a siren). Both are in Settings → *Notify & prompt*, each with a **Test** button and an optional command override (run directly, never through a shell). A configured Notification hook **wins**: the native alert is not played on top of it, so an alert is never doubled. The payload still carries `session_title`, `session_id` and `project`. |
| **Bulk delete** | Hold **Alt**: every session row shows a ✕ (delete that one) and each project header offers *Delete all N listed…*, which names the sessions and asks once. Transcript files stay on disk. |
| **Sidebar** | *Pinned* and project groups are collapsed by default; a dot shows a working session (pulsing) or one with running background tasks, also on collapsed group headers. A session that finished in the background gets a blue dot and an entry in **Needs attention** (pinned sessions are listed there too — a collapsed Pinned group shows only a dot): clicking the dot acknowledges it (the light goes out, the entry stays), the block's ✓ clears the dot and its ✕ removes the entry, and **removing all of them** is the ✕ in the block header — which, like the other bulk actions, appears only while **Alt** is held, and says in the transcript which sessions it removed. Writing to a session never clears its entry on its own — only another finished turn makes it wait again. A turn that ends in the session you are **currently looking at** is held back and enters the block the moment you switch away (or at the next start), because the block lists what you *cannot* see: it used to be skipped entirely, so a session that finished while you watched it was missing from the one list that answers "what is waiting for me?" (reported twice). |
| **Continue after a restart** | At startup KvindoCode offers to continue the sessions that were active when it last was alive (`LastRunEnded`, refreshed every 20 s; a session counts if its newest message is within 15 min of it — `AppSettings.WasActiveInPreviousRun`). Continuing sends that session a real user turn: *“The app was restarted, please continue where you left off. Note that background tasks and loops will not be autocontinued, so you have to restore them yourself.”* The dialog offers *Continue all / Choose individually / Skip all*. |
| **Terminals** | The icon beside a project's **+** opens a **2×2 grid of real terminals** (main area; *Back to session* closes it). Each pane is an **XTerm emulator** (`Iciclecreek.Avalonia.Terminal`: XTerm.NET over a PTY) — not text this app paints — so the screen model, colours, selection, scrollback, alternate screen (vim, htop, less) and graphics are the emulator's, and it repaints on a shared throttled frame instead of a poll loop. Each pane runs **`tmux new-session -A -s kv-<hash>-<n>`**, so the scrollback lives in the tmux server: **closing or restarting the app keeps every pane**, and the same pane can be attached from a real terminal (the session name is in the pane's tooltip). Panes start in the project directory, are resizable — a splitter drag moves that boundary in tmux by the cells it moved on screen, and the sizes are **remembered per project** — can be **renamed** (the name goes to tmux, so it survives a restart) and closed. The ✕ in the grid header **restarts** the four terminals (fresh shells, same view). Typing goes to the pane you clicked. Nothing is written to disk, so terminal output never lands outside the secret audit; a machine without tmux explains itself instead of showing an empty grid. |
| **UI** | Left-aligned transcript, collapsible tool cards with diffs, closable task list, light/dark theme. |

The header has a **find-in-session** box (case-insensitive regexp, literal if invalid): Enter / Shift+Enter step through the matches, which are real selections and can be copied; Esc clears it. The **Notification** hook payload carries `session_title`, `session_id` and `project`, so a desktop notification can name the session.

Keys: `Enter` send · `Shift+Enter` newline · `Shift+Tab` plan/regular · `Esc` stop · `Ctrl+N` new session · `Ctrl+O` open folder · `Ctrl+F` search · `Ctrl+,` settings · hold `Alt` in the sidebar to reveal delete on every row (the per-project button lists the sessions and asks once; a row's trash deletes at once).

## Develop
Needs the **.NET 10 SDK** (Avalonia 12 is built against it). A user-local SDK is enough:
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet`
— the build scripts pick it up when `dotnet` on PATH is older.

```bash
./tools/test.sh                      # ~1230 tests, ~12 min — skips the real-model class
./tools/test.sh --all                # everything, ~26 min (needs a local vllm on 127.0.0.1:8001)
./tools/test.sh --filter FullyQualifiedName~SomeTests
python3 tools/update_scores.py       # refresh src/KvindoCode.App/Assets/scores.json
dotnet run --project src/KvindoCode.App -- /path/to/project
```
`tools/test.sh` is preferred over a bare `dotnet test`: it nices the build so the desktop stays usable, and it excludes
`Category=RealLlm` — `SecretAuditorRealUiTests` runs a real inference per audited tool output, which is 10 of a 26-minute
run on its own. `KVINDOCODE_SCRIPT=file.json` swaps the API for a scripted fake (UI tests/demos); `KVINDOCODE_HOME`
relocates `~/.kvindocode`.

## FAQ

**Is this a GUI for an existing coding agent, or its own agent?** Its own: the agent loop, tools, plan review and
subagents are implemented here, talking to any `/v1/chat/completions` gateway. The session format is compatible with a
Claude-style store, so existing sessions can be opened (see *Projects / sessions* above).

**Why a desktop app instead of a terminal UI?** Cards, diffs, a file tree, a side panel, scrollable history and a real
terminal grid are hard to do well in a terminal, and a GUI can hold several sessions side by side.

**Does it need a specific model?** No. It is model-agnostic: the picker lists what the gateway serves, with per-model
price, context, effort and benchmark columns. Planning and review want a strong model; titles and classification use a
cheap one.

**How does parallel work not destroy the repository?** Every session gets its own `git worktree` and branch
(`kv/<session>`); nothing is committed or merged for you, and the app never merges. The header chip copies the merge
command for that session's branch.

**What happens to secrets it sees?** They are detected by patterns first (PEM blocks, kubeconfig, JWTs, vendor token
shapes, credentials in URLs, secret-named assignments), stored encrypted, and replaced by reversible markers in what
the model sees. A local model is only a second opinion, and its findings go through a dialog. Anything that reached a
transcript before it was known is registered for rotation.

**Does it work offline / with a local model?** The agent needs an endpoint; point it at a local [OI]-compatible server
and it works. Detection, masking and the vault are local and need no model at all.

**Platforms?** Linux (primary), Windows and macOS. Needs the .NET 10 SDK to build; releases are self-contained.
