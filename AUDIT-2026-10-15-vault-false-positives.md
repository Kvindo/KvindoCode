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

The names, exactly as they appear in the Secrets list (24 from the guard + `audited-token-a1c58f74a01e`, which the
first draft of this file called a defect — see §3):

```
audited-password-07d97f65a6eb   audited-password-6efdca836ced   audited-password-8fc27d1e1a4a
audited-password-af89dc4ac01e   audited-password-b63fede1644f   audited-password-b84f3b061910
audited-password-b878d7ad7c68   audited-password-db52ca2260bd   audited-password-f9e6cd68f985

audited-token-04abb085cbcc      audited-token-06a5d25b834b      audited-token-21c9b930da83
audited-token-2f12d0e2e795      audited-token-348fb4e89ac8      audited-token-42bd34b2e3b1
audited-token-505b88699a41      audited-token-7a75352f474e      audited-token-a1c58f74a01e
audited-token-a407732cb82b      audited-token-a96ad3fca960      audited-token-b180730e0e89
audited-token-c1c3a968032e      audited-token-c7290f43c3c9      audited-token-d5c363a1be3e
audited-token-f3329665a6ab
```

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

## 4. Decision taken (user, 2026-10-15)

**All 24 were marked "not a secret"; `audited-token-a1c58f74a01e` was marked as a secret and kept.**

Verified in the vault afterwards:

| | before | after |
|---|---|---|
| `secrets` | 713 | 739 (the 24 removed, new ones detected since) |
| `excludedSha256` | 1001 | 1045 |
| `nonSecrets` | 959 | 1003 |
| of the 25 named here, still in the vault | 25 | **1** (`audited-token-a1c58f74a01e`) |

The one kept entry is also the one case worth knowing about: it is matched by our own detector (the `sos…eos`
start/end-marker rule), so with it still stored it is masked outbound, and the review list will keep offering it for
deletion — its *shape* (52 chars, contains a backslash) is what the shape rule objects to. That is now an explicit,
named allowance in the live-vault guard test
(`SecretAuditorRealUiTests.No_secret_currently_stored_is_described_as_a_false_positive`), alongside the two entries
confirmed on 2026-10-10, so the guard stays honest for everything else.

`audited-token-a1c58f74a01e` is also a good illustration of why the detector's plausibility filter, not the shape
rule, decides what is real: the detector matches it, the plausibility filter rejects it (it carries a backslash, a
code-ish fragment), and only the human could say which was right. They said keep.

Nothing else was changed in the vault.

