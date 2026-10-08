# KvindoCode UI test cases — secret auditing / vault pipeline

Manual + automation reference for verifying the secret pipeline end to end (auditor → vault → masking →
transcript → model). Companion to `AUDIT-2026-10-03.md` / `AUDIT-2026-10-03-r2.md`, which explain *why* these
gaps exist.

Marker text in this document is written `%[ $NAME$ ]%` (spaces added) on purpose: the literal form is
expanded/rejected by the Write tool — itself a test case (see **Case 11**).

## Conventions

* **Test secret fixtures** (create once in a scratch dir passed to the session, never inside the repo):
  * `test.pem` — a real RSA private key (openssl genrsa 2048), host key.
  * `ssl.key` + `ssl.crt` — a self-signed pair (`openssl req -x509 -newkey rsa:2048 -nodes`).
  * `kubeconfig` — a valid kubeconfig whose `client-key-data` is a base64 key and whose token is a 64-char JWT.
  * `app.env` — `DATABASE_PASSWORD=<32 random chars>`, `API_TOKEN=<40 random chars>`.
  Use **unique** random values per run so a leak can be grepped byte-for-byte.
* **"Masked" means**: the transcript / tool result / tool-call arguments show the marker or `«name»`, and the
  raw value is absent from `~/.kvindocode/projects/**/*.jsonl`, from `~/.kvindocode/search-index/*.txt`, and from the
  request body the API receives.
* **Automation seam**: `tests/KvindoCode.Tests/SecretAuditorIntegrationTests.cs` already provides
  `Sandbox` (isolated `KVINDOCODE_HOME`), a `ScriptedAuditor`, `ScriptedLlmClient` (`Script.*`) and
  `FakeInteraction` (incl. `SecretConfirmationDecisions`). Prefer extending it over new UI automation.
* Cases 1–5 (the original list) are already implemented twice: as engine-level scenarios in
  `tests/KvindoCode.Tests/SecretAuditorIntegrationTests.cs` (`Scenario_6_1` … `Scenario_6_5`) and as headless-UI
  scenarios in `tests/KvindoCode.Tests/SecretAuditorUiTests.cs` (`Scenario_6_*_UI`, Avalonia headless, drives a real
  `MainWindow`). The cases below continue the list as **6–15** — none of them is implemented yet.

## Already covered (original list)

| # | Scenario | Engine test (`SecretAuditorIntegrationTests`) | UI test (`SecretAuditorUiTests`) |
|---|---|---|---|
| 1 | read all KvindoCode sources with audit on — auditor must not trigger; then a PEM must be handled | `Scenario_6_1_Read_kvindocode_source_with_audit_on_does_not_falsely_block` | `Scenario_6_1_UI_…_renders_in_transcript_without_blocking` |
| 2 | read PEM / SSL key / SSL cert / kubeconfig — must be masked | `Scenario_6_2_Read_PEM_SSL_key_cert_and_kubeconfig_are_masked` | `Scenario_6_2_UI_Read_RSA_key_and_kubeconfig_are_masked_in_UI_transcript` |
| 3 | same, but performed by a subagent | `Scenario_6_3_Subagent_audits_and_masks_secrets` | `Scenario_6_3_UI_Subagent_reads_secret_…_with_audit` |
| 4 | protected read, then audit disabled, then second read must stay unmasked | `Scenario_6_4_Session_audit_toggle_disabled_leaves_secrets_unmasked` | `MainWindow_Audit_button_toggles_session_override_and_updates_visual_state` |
| 5 | low-confidence finding → confirmation window decision | `Scenario_6_5_Low_confidence_finding_prompts_confirmation_dialog` | `MainWindow_UiInteraction_wires_ConfirmSecret_and_returns_valid_decision` |

---

## Case 6 — Read → Write round trip (placeholder expansion back to disk)

**Goal:** masking must not corrupt the file the agent writes, and the plaintext must reach disk *only* where it
belongs.

1. Audit on. `Read` `test.pem` (expect the marker in the result).
2. Order: "copy the content you just read into `<scratch>/copy.pem`" (Write), then "change line 3 to …" (Edit
   whose `new_string` re-inserts a line containing the key body).
3. Diff `copy.pem` against the original.

**Pass:** `copy.pem` is byte-identical (or differs only by the requested edit); the transcript, the tool-call
arguments of Write/Edit and the side panel (click the card) show only markers.
**Fail signals:** the file contains a marker literally; the file is empty/short; Edit reports
"Unknown or unavailable secret placeholder".
**Automate:** `ToolTests`-style test that asserts `File.ReadAllText(written)` contains the fixture value, and that
`ctx.Session` history JSON never does.

## Case 7 — Nothing persisted holds the plaintext

**Goal:** the value exists only in the vault at rest.

1. Run Case 6.
2. Close and reopen the session (and restart the app).
3. Search (`Ctrl+F`) for a 12-char fragment of each fixture value.

**Pass:** zero hits in results and in `grep -r <fragment>` over `~/.kvindocode/projects`, `~/.kvindocode/search-index`,
`~/.kvindocode/plans`, `~/.kvindocode/screenshots`; the transcript still renders the marker after reload.
**Fail signals:** any hit, or a marker that fails to re-render (raw marker text shown as if user text).

## Case 8 — Hand the value to a command without the model seeing it

**Goal:** the documented `Secrets get` → `$(cat PATH)` flow works and leaves nothing behind.

1. Order: `Secrets get` for a stored value with `to=file`; then a Bash call that proves possession, e.g.
   `sha256sum "$(cat <PATH>)"` compared against `sha256sum` of the known fixture value.
2. Note the returned path; check permissions and lifetime.

**Pass:** the hash matches (the value really was used); neither the tool result nor the transcript contains the
value; the file is mode `0600` inside `~/.kvindocode/secret-out/`; it is deleted when the session closes, and a
leftover older than 12 h is swept on the next `Secrets` call (`AgentSession.RegisterSecretFile`).
**Fail signals:** the value appears in the tool result; the file is world-readable; the file survives session
close.

## Case 9 — Marker-shaped text written by the agent

**Goal:** the agent can document the marker syntax, and unknown markers never become silent plaintext.

1. Order: "create `docs/marker.md` explaining the secret placeholder syntax, with one real marker-shaped
   example whose name is not in the vault".
2. Then: "create `docs/known.md` using the marker for a value that *is* in the vault".

**Pass:** `marker.md` either stores the literal text or the tool fails with an explicit, actionable error — it
must not write a silently-expanded value; `known.md` contains the real value (that is the designed behaviour)
and a notice explains the expansion.
**Fail signals:** unknown marker written and later expanded into nonsense; write silently dropped the content.
**Note:** current behaviour is "hard error" (`FileTools.cs:200`) — this case pins down which behaviour is wanted.

## Case 10 — Per-session audit override survives restart

**Goal:** the shield toggle is per session, persists, and does not leak into other sessions.

1. Audit on; read a secret file (masked). Assert masking happened.
2. Click the shield → auditing OFF for this session; read a second secret file with different values.
3. Restart the app, reopen the same session; read a third file.
4. Open a **new** session in the same project; read a fourth file.

**Pass:** step 2 and 3 return the values unmasked in that session only (transcript shows them, and the
"audit off" icon state is restored); step 4 is masked again.
**Fail signals:** the override resets on restart, or leaks to the new session; the session's registry/meta line
loses `AuditSecrets`.

## Case 11 — Subagent inheritance and containment

**Goal:** a child session cannot be used to exfiltrate what the parent would have hidden.

1. Audit on. Order: "spawn a subagent that reads `test.pem` and `kubeconfig` and reports a one-line summary of
   what is inside".
2. Watch the `Agent` tool card and `AgentOutput`.

**Pass:** the subagent's own transcript is masked; the summary that reaches the parent is masked; the parent
cannot recover the value from `AgentOutput`, from the tool card, or from the parent transcript.
**Fail signals:** plaintext in `AgentOutput`; the subagent ran with auditing off; no `[Read]` marker in the
child output.
**Automate:** `Scenario_6_3` extension that asserts on the parent's `AgentOutput` result, not only the child.

## Case 12 — Secret inside an image

**Goal:** an image that carries a credential is dropped, not sent.

1. Prepare a PNG that visibly contains `API_TOKEN=<fixture>` (render text into an image).
2. Audit on. `Read` the image (vision model) or open it in `Browser` and screenshot.
3. Then send a normal text follow-up.

**Pass:** the image part is stripped from the outbound request, an inline notice says the image was dropped
because it contained a secret, and the turn continues (text-only) instead of failing or hanging.
**Fail signals:** the image reaches the API; no notice; the turn ends with an error.
**Note:** image auditing is a vision pass over base64 data — record the latency, it must not stall the turn.

## Case 13 — Auditor outage mid-session (fail closed)

**Goal:** no silent degradation when the auditor dies.

1. Session idle, audit on. Stop the auditor service (`systemctl stop secret-auditor` or kill the vLLM).
2. Send a message that makes the agent read `app.env`.
3. Restart the auditor; send another message.

**Pass:** step 2 sends **nothing** to the cloud, the tool output is withheld, and the UI shows a clear
"auditor unavailable" state — not the apology text presented as the assistant's answer. Step 3 recovers
automatically.
**Fail signals:** the request goes out unaudited; the turn ends as "done" with the auditor error as content
(`FinishReason == "audit_failed"` currently has no consumer — this is the case that will catch it).
**Automate:** `ScriptedAuditor` returning `IsError` for the first call, asserting the inner client was never
invoked.

## Case 14 — Auto-compaction must not carry a secret

**Goal:** the summary is as protected as the messages it replaces.

1. Put a secret in an early user message (paste `APP_ENV` content) and let the agent read a secret file.
2. Keep working until the context bar reaches 80 % so a compaction runs.
3. Scroll to the "conversation compacted" card; then continue a turn and watch what is sent.

**Pass:** the summary text (card, transcript, and the context used afterwards) contains no plaintext; the
summarisation request itself passed through the auditor; the "N earlier messages are not shown" notice after a
reload is consistent with what is actually hidden.
**Fail signals:** plaintext in the summary; the compact card tooltip leaks the raw text.

## Case 15 — Rewind / fork with secrets

**Goal:** history editing does not resurrect a value or lose the vault entry.

1. Read a secret file, then a few more turns.
2. Hover an earlier user message → ↶ rewind; and in a second run ⑂ fork from the same point.

**Pass:** after rewind the context and the transcript re-issue contain no plaintext; the forked session's
transcript and its search index contain none; the vault entry is intact and `Secrets get` still works in both
sessions; the original branch on disk is unchanged.
**Fail signals:** a shared `ChatMessage` instance lets the fork mutate the parent's history (in-place masking),
or the rewound branch leaves an orphan tool result that re-injects the value.
**Note:** rewind/fork copy `Entry` objects; check they deep-copy the message (see audit finding F3).

---

## Cross-cutting checks worth running after each case

* `grep -rF <fragment>` over `~/.kvindocode/projects`, `~/.kvindocode/search-index`, `~/.kvindocode/secret-out`,
  `~/.kvindocode/usage-debug.log` (yes — the debug log is on the list, see audit finding S10).
* The tool card **and** the side-panel "Details" view (they are rendered from different fields).
* The session title and the sidebar entry (titles are masked too).
* `/loop`-style scheduled prompts and background task output (`TaskOutput`) — same masking path, easy to miss.
