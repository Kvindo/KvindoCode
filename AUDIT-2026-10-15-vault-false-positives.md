# Vault entries the false-positive test flags — what they are, and what deleting them costs

Date: 2026-10-15. Scope: the ~25 entries listed by
`SecretAuditorRealUiTests.No_secret_currently_stored_is_described_as_a_false_positive`, which reads the **real**
vault at `~/.kvindocode/secrets.vault.json` (~717 entries) and fails when an entry it calls a false positive also
looks like a credential.

No value is printed anywhere below. Every fact comes from metadata (name, length, creation date, provenance note),
character classes and the code's own classifiers.

## 1. What the test actually checks

For every stored value it asks `SecretShapes.Describe(value)` — the same function that drives **Secrets → "show
entries that look like false positives"** and the **"Delete all false positives…"** action. An entry is reported
when BOTH are true:

1. `Describe` says suspicious (a shape rule fired), **and**
2. the value is "credential-like" by the test's second opinion: `length >= 16` and no whitespace — unless it is a
   ULID / generated identifier / one of our own artefacts, which are expected to be flagged.

So the test is: *"an entry the review list offers for deletion must not look like a real credential"*. It is a guard
on the **bulk purge**, not on the detector.

## 2. What they are (shape reason → count)

| `Describe` reason | count | what that means |
|---|---|---|
| `a path or name, not a secret value` | 16 | starts with `/`, or `NNname/`, or `word/word/word` |
| `a file path, not a secret value` | 2 | contains a separator and ends in a file extension |
| `an identifier, not a secret value` | 4 | 40-120 chars, letters/`_`/`.` only |
| `a variable reference, not a secret value` | 3 | `${X}` / `$X` / `{{X}}` / `<X>` |
| `code, not a secret value` | 1 | a call or method chain, e.g. `name(...)` |
| `a regex or escape fragment` | 1 | ends in `\` or holds `{n,m}` |

Grouped by what they look like:

- **18 of 25 start with `/`** (16 "path or name" + 2 "file path"). Lengths 47-144, entropy 4.9-5.25. Eight of them
  are 118-122 characters and were all created **2026-10-13 18:29** in one tool output — one batch from a single
  command. They are tagged `audited-token`.
- **Provenance is uniform**: every one was auto-stored by the detector "detected in tool output at …" (or
  "detected deterministically in an outbound request at …"). None came from the human storing a value by hand, and
  none was ever marked excluded.
- **most are mixed-case, all of them contain a symbol**, so they are not the lowercase-base32 identifier class the
  vault's earlier cleanup targeted.

Note on deletion: removing an entry only removes a **stand-in for an identifier**, not a credential — *unless* the
value is a credential that happens to look like a path, in which case deleting the entry unmasked it per the
"Delete" choice of the purge. The transcripts keep whatever was in them either way.

## 3. Is any of them a credential the detector would still catch? No.

Checked every flagged entry against the detector's own plausibility filter (`IsPlausibleSecretValue`), the same gate
the outbound path uses before it stores or masks anything:

| | count |
|---|---|
| entries the guard reports | 25 |
| of those, plausible per the detector | **0** |

So not one of them is a value the detector would offer as a secret. That also disposes of the one case that looked
like a defect: `audited-token-a1c58f74a01e` (52 chars) *is* matched by a detector rule, and I first read that as
"the app says credential, the review list says junk". It is not — the rule that matches it is the deliberate one for
the user's own description of a detector pattern (`sos…eos`), the value contains a backslash, and the plausibility
filter rejects it too. A rule was briefly added to `SecretShapes.Describe` to prefer detector evidence over shape
rules; it is **reverted** — it broke 8 reported-false-positive tests, because those very tokens are matched by
detector rules as well.

What is left is therefore a plain human judgement call about masking, not a correctness bug.

## 4. Options (your call — the purge is one click away in the UI)

| Option | Effect | Risk |
|---|---|---|
| **A. Delete all 25** | ~25 of 717 entries go; the review list is clean; the guard test passes. | If one of the 16 path-like values is a credential that merely looks like a path, it stops being masked in new requests. |
| **B. Keep them, mark reviewed** | Add them to a reviewed allow-list the test honours (as it already does for two entries the user confirmed on 2026-10-10). | The test keeps needing hand-maintained exceptions; the list stays dirty. |
| **C. Leave as is** | Nothing changes; the guard test keeps failing on every full run. | The failure hides a new one if it ever appears. |
| **D. Inspect before deciding** | Open **Secrets**, filter "looks like false positives", and check the handful you recognise (I can walk you through it, or print lengths/prefix-classes per row). | Takes your time; safest. |

My recommendation: **D for the 16 path-like ones, A for the rest** — an `audited-password` whose value is
`${VAR}` or `name(...)` cannot be a password, so deleting it is free. The path-like batch is the only group where a
wrong call costs masking.

